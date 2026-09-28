using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Abbyy.DocLang.Demo;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var fineReader = builder.Configuration.GetSection("FineReader").Get<FineReaderOptions>() ?? new();
var processing = builder.Configuration.GetSection("Processing").Get<ProcessingOptions>() ?? new();
processing.Validate();
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = processing.MaxUploadBytes;
    server.AddServerHeader = false;
});
builder.Services.AddSingleton(fineReader);
builder.Services.AddSingleton(processing);
builder.Services.AddSingleton<BenchmarkTokenizer>();
builder.Services.AddSingleton<IDocumentProcessor, FineReaderProcessor>();
builder.Services.AddSingleton<DocumentService>();
builder.Services.AddSingleton<SessionSettings>();
builder.Services.AddSingleton<DemoFeatureService>();
builder.Services.AddSingleton<ApplicationSettingsService>();
// A plain HttpClient avoids request/header logging handlers. Only a per-request Authorization header holds the key.
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(120) });
builder.Services.AddSingleton<IAiProvider, OpenAiProvider>();
builder.Services.AddSingleton<AiComparisonService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
var app = builder.Build();
var staticFiles = new EmbeddedFileProvider(typeof(Program).Assembly,
    typeof(Program).Assembly.GetName().Name + ".wwwroot");
var indexFile = staticFiles.GetFileInfo("index.html");
if (!indexFile.Exists) throw new InvalidOperationException("The embedded application UI is missing. Rebuild the application.");
string indexTemplate;
using (var reader = new StreamReader(indexFile.CreateReadStream())) indexTemplate = reader.ReadToEnd();
var stampedIndex = ApplicationIdentity.StampIndex(indexTemplate);

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    try
    {
        if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
            throw new DemoException("This application accepts localhost requests only.", 403);
        if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/application")
        {
            var contractVersion = context.Request.Headers[ApplicationIdentity.ContractHeader].ToString();
            var buildId = context.Request.Headers[ApplicationIdentity.BuildHeader].ToString();
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value?.EndsWith("/download", StringComparison.Ordinal) == true)
            {
                if (contractVersion.Length == 0) contractVersion = context.Request.Query["contract"].ToString();
                if (buildId.Length == 0) buildId = context.Request.Query["build"].ToString();
            }
            if (!ApplicationIdentity.Matches(contractVersion, buildId))
                throw new DemoException(
                    "Application version mismatch. Reload the page. If the message remains, stop the demo, rebuild it, and start it again.",
                    409, "application_version_mismatch");
        }
        if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method))
        {
            var origin = context.Request.Headers.Origin.ToString();
            var sameOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            if (context.Request.Headers["X-Demo-Request"] != "1"
                || (origin.Length > 0 && !string.Equals(origin, sameOrigin, StringComparison.OrdinalIgnoreCase)))
                throw new DemoException("The request must come from this demo page.", 403);
            if (context.Request.Path != "/api/process"
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = 16384;
        }
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception exception)
    {
        // Do not log request bodies, credentials, native exceptions, or provider response bodies.
        if (context.Response.HasStarted) { context.Abort(); return; }
        var (status, message) = exception switch
        {
            DemoException demo => (demo.StatusCode, demo.Message),
            BadHttpRequestException bad => (bad.StatusCode, "The request is invalid or exceeds the size limit."),
            JsonException => (400, "The settings or question could not be read."),
            _ => (500, "The operation could not be completed. Check configuration and try again.")
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            error = message,
            code = (exception as DemoException)?.Code
        });
    }
});

app.Use(async (context, next) =>
{
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && (context.Request.Path == "/" || context.Request.Path == "/index.html"))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsGet(context.Request.Method)) await context.Response.WriteAsync(stampedIndex);
        return;
    }
    await next(context);
});
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFiles });
app.MapGet("/api/application", () => ApplicationIdentity.Status);
app.MapGet("/api/status", () => new
{
    maxUploadBytes = processing.MaxUploadBytes,
    fineReaderConfigured = !string.IsNullOrWhiteSpace(fineReader.CustomerProjectId),
    tokenizer = BenchmarkTokenizer.Encoding
});
app.MapGet("/api/settings", (ApplicationSettingsService settings) => settings.Status);
app.MapPut("/api/settings", async (HttpRequest request, ApplicationSettingsService settings) =>
{
    var update = await request.ReadFromJsonAsync<SettingsUpdate>() ?? throw new DemoException("Settings are required.");
    try
    {
        return settings.Update(update);
    }
    finally { update.ApiKey = null; }
});
app.MapPost("/api/ai/test-connection", async (SessionSettings settings, IAiProvider provider, CancellationToken cancellationToken) =>
    await provider.TestConnectionAsync(settings.GetCredentials(), cancellationToken));
app.MapGet("/api/result", (DocumentService documents) => Results.Json(documents.Current?.Summary));
app.MapDelete("/api/result", (DocumentService documents) => { documents.Clear(); return Results.NoContent(); });
app.MapPost("/api/process", async (HttpRequest request, DocumentService documents, CancellationToken cancellationToken) =>
{
    if (request.ContentLength > processing.MaxUploadBytes) throw new DemoException("The document exceeds the upload limit.", 413);
    var fileName = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString());
    return (await documents.ProcessAsync(request.Body, fileName, cancellationToken)).Summary;
});
app.MapGet("/api/exports/{id:guid}/{kind}/content", (Guid id, string kind, DocumentService documents) =>
    Results.Json(new { content = documents.Get(id).GetExport(kind).Content }));
app.MapGet("/api/exports/{id:guid}/{kind}/download", (Guid id, string kind, DocumentService documents) =>
{
    var export = documents.Get(id).GetExport(kind);
    return Results.File(export.Bytes, "application/octet-stream", export.FileName);
});
app.MapPost("/api/ask", async (HttpRequest request, AiComparisonService comparison, CancellationToken cancellationToken) =>
{
    var input = await request.ReadFromJsonAsync<AiQuestion>(cancellationToken)
        ?? throw new DemoException("A question is required.");
    return await comparison.AskAsync(input, cancellationToken);
});
app.MapPost("/api/ai/compare", async (HttpRequest request, AiComparisonService comparison, CancellationToken cancellationToken) =>
{
    var input = await request.ReadFromJsonAsync<AiComparisonQuestion>(cancellationToken)
        ?? throw new DemoException("A question is required.");
    return await comparison.CompareAsync(input, cancellationToken);
});
app.Run();

public partial class Program;
