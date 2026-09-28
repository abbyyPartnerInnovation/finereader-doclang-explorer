using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Abbyy.DocLang.Demo.Tests;

internal sealed class DemoFactory : WebApplicationFactory<Program>
{
    public FakeProcessor Processor { get; } = new();
    public FakeAiProvider AiProvider { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDocumentProcessor>();
            services.AddSingleton<IDocumentProcessor>(Processor);
            services.RemoveAll<IAiProvider>();
            services.AddSingleton<IAiProvider>(AiProvider);
        });
    public HttpClient DemoClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        client.DefaultRequestHeaders.Add("X-Demo-Request", "1");
        client.DefaultRequestHeaders.Add(ApplicationIdentity.ContractHeader, ApplicationIdentity.ContractVersion);
        client.DefaultRequestHeaders.Add(ApplicationIdentity.BuildHeader, ApplicationIdentity.BuildId);
        return client;
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task BuiltFrontendAndBackendExposeAndEnforceOneIdentity()
    {
        await using var factory = new DemoFactory();
        using var raw = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var identity = (await raw.GetFromJsonAsync<ApplicationIdentityStatus>("/api/application"))!;
        Assert.Equal(ApplicationIdentity.ContractVersion, identity.ContractVersion);
        Assert.Equal(ApplicationIdentity.BuildId, identity.BuildId);

        var index = await raw.GetStringAsync("/");
        Assert.Contains($"content=\"{identity.ContractVersion}\"", index);
        Assert.Contains($"content=\"{identity.BuildId}\"", index);
        Assert.DoesNotContain("__APP_BUILD_ID__", index);
        Assert.Contains("verifyApplicationIdentity", await raw.GetStringAsync("/app.js"));

        using var missingIdentity = await raw.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Conflict, missingIdentity.StatusCode);
        Assert.Contains("application_version_mismatch", await missingIdentity.Content.ReadAsStringAsync());

        raw.DefaultRequestHeaders.Add(ApplicationIdentity.ContractHeader, ApplicationIdentity.ContractVersion);
        raw.DefaultRequestHeaders.Add(ApplicationIdentity.BuildHeader, "different-build");
        using var wrongIdentity = await raw.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Conflict, wrongIdentity.StatusCode);

        using var current = factory.DemoClient();
        using var settings = await current.GetAsync("/api/settings");
        settings.EnsureSuccessStatusCode();
        var settingsJson = await settings.Content.ReadAsStringAsync();
        Assert.Contains("\"enableCompareAll\"", settingsJson);
        Assert.DoesNotContain("showEstimatedCost", settingsJson, StringComparison.OrdinalIgnoreCase);
        using var compareRoute = await current.GetAsync("/api/ai/compare");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, compareRoute.StatusCode);
    }

    [Fact]
    public async Task SettingsRevisionRejectsStaleAndMixedUpdatesWithoutPartialMutation()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var revision = initial.RootElement.GetProperty("revision").GetInt64();

        using var changed = await client.PutAsJsonAsync("/api/settings", new
        {
            demoFeature = "showJson", demoFeatureEnabled = false, expectedRevision = revision
        });
        changed.EnsureSuccessStatusCode();

        using var stale = await client.PutAsJsonAsync("/api/settings", new
        {
            model = "stale-model", expectedRevision = revision
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("settings_revision_conflict", await stale.Content.ReadAsStringAsync());

        using var mixed = await client.PutAsJsonAsync("/api/settings", new
        {
            apiKey = "must-not-be-applied", experiencePreset = "Custom"
        });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);

        using var current = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        Assert.False(current.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal(SessionSettings.DefaultModel, current.RootElement.GetProperty("model").GetString());
        Assert.Equal("Custom", current.RootElement.GetProperty("demoFeatures").GetProperty("preset").GetString());
        Assert.False(current.RootElement.GetProperty("demoFeatures").GetProperty("features").GetProperty("showJson").GetBoolean());

        using var obsolete = await client.PutAsJsonAsync("/api/settings", new
        {
            demoFeatures = new { showEstimatedCost = true }
        });
        Assert.Equal(HttpStatusCode.BadRequest, obsolete.StatusCode);
    }

    [Fact]
    public async Task RealApiProcessesOnceAndDownloadsOriginalBytesWithoutAiConfiguration()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var request = Upload();
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ComparisonResult>())!;
        Assert.Equal(1, factory.Processor.Calls);
        Assert.False((await client.GetFromJsonAsync<SettingsStatus>("/api/settings"))!.Configured);
        foreach (var export in Fixtures.Result().Exports)
        {
            var path = "/api/exports/" + result.Id + "/" + export.Kind.ToString().ToLowerInvariant();
            Assert.Equal(export.Bytes, await client.GetByteArrayAsync(path + "/download"));
            using var content = JsonDocument.Parse(await client.GetStringAsync(path + "/content"));
            Assert.Equal(export.Content, content.RootElement.GetProperty("content").GetString());
        }
        Assert.Equal(1, factory.Processor.Calls);
        Assert.False(Directory.Exists(factory.Processor.Directory));
        using var page = await client.GetAsync("/");
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task SettingsRoundTripNeverReturnsKeyAndRejectsCrossOriginChanges()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        var secret = "test-" + Guid.NewGuid().ToString("N");
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = secret, model = "test-model" });
        saved.EnsureSuccessStatusCode();
        Assert.DoesNotContain(secret, await saved.Content.ReadAsStringAsync());
        Assert.DoesNotContain(secret, await client.GetStringAsync("/api/settings"));
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        { Content = JsonContent.Create(new { clearKey = true }) };
        crossOrigin.Headers.Add("Origin", "https://unrelated.example");
        using var blocked = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.True((await client.GetFromJsonAsync<SettingsStatus>("/api/settings"))!.Configured);
        client.DefaultRequestHeaders.Remove("X-Demo-Request");
        using var missingHeader = await client.PutAsJsonAsync("/api/settings", new { clearKey = true });
        Assert.Equal(HttpStatusCode.Forbidden, missingHeader.StatusCode);
    }

    [Fact]
    public async Task FeatureChangesPreserveConfiguredApiKeyAndDoNotNeedADocument()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        var secret = "test-" + Guid.NewGuid().ToString("N");
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = secret });
        saved.EnsureSuccessStatusCode();
        using var changed = await client.PutAsJsonAsync("/api/settings", new
        {
            demoFeatures = new { showJson = false, showPlainText = true, showBenchmarkTokens = true,
                showFileSizes = true, showProcessingTime = true, showTokenReduction = true,
                showRepresentationAnalysis = true, showDocLangViewer = true, enableAiTest = true, showAiResponses = true }
        });
        changed.EnsureSuccessStatusCode();
        var body = await changed.Content.ReadAsStringAsync();
        Assert.Contains("\"preset\":\"Custom\"", body);
        Assert.DoesNotContain(secret, body);
        Assert.True((await client.GetFromJsonAsync<SettingsStatus>("/api/settings"))!.Configured);
        Assert.Equal(0, factory.Processor.Calls);
        Assert.Empty(factory.AiProvider.Requests);
    }

    [Fact]
    public async Task TestConnectionDoesNotRequireADocumentAndUsesTheConfiguredModel()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = "test-secret" });
        saved.EnsureSuccessStatusCode();
        using var tested = await client.PostAsync("/api/ai/test-connection", null);
        tested.EnsureSuccessStatusCode();
        Assert.Equal(SessionSettings.DefaultModel, factory.AiProvider.ConnectionModel);
        Assert.Equal(0, factory.Processor.Calls);
    }

    [Fact]
    public async Task AskSupportsASelectedStoredRepresentationWithoutReprocessing()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var processed = await client.SendAsync(Upload());
        var document = (await processed.Content.ReadFromJsonAsync<ComparisonResult>())!;
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = "test-secret", model = "test-model" });
        using var asked = await client.PostAsJsonAsync("/api/ask", new { documentId = document.Id, question = "What is here?", representation = "json" });
        asked.EnsureSuccessStatusCode();
        var answerJson = await asked.Content.ReadAsStringAsync();
        Assert.DoesNotContain("estimatedCost", answerJson, StringComparison.OrdinalIgnoreCase);
        var answer = JsonSerializer.Deserialize<AiOperationResult>(answerJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("json", answer.Representation);
        Assert.Equal(document.Id, answer.DocumentId);
        Assert.Equal(Fixtures.Json, factory.AiProvider.Requests.Single().Document);
        Assert.Equal(document.Exports.Single(export => export.Kind == "json").Tokens, answer.BenchmarkTokens);
        Assert.Equal(Encoding.UTF8.GetByteCount(Fixtures.Json), answer.AiInputBytes);
        Assert.Equal(120, answer.ActualInputTokens);
        Assert.Equal(20, answer.ActualCachedInputTokens);
        Assert.Equal(15, answer.ActualOutputTokens);
        Assert.Equal(135, answer.ActualTotalTokens);
        Assert.Equal(12, answer.ResponseMilliseconds);
        Assert.Equal(1, factory.Processor.Calls);
    }

    [Fact]
    public async Task AskKeepsLocalMeasurementsWhenProviderUsageIsAbsent()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var processed = await client.SendAsync(Upload());
        var document = (await processed.Content.ReadFromJsonAsync<ComparisonResult>())!;
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = "test-secret", model = "test-model" });
        factory.AiProvider.Usage = null;

        using var asked = await client.PostAsJsonAsync("/api/ask", new { documentId = document.Id, question = "What is here?", representation = "plaintext" });
        asked.EnsureSuccessStatusCode();
        var answer = (await asked.Content.ReadFromJsonAsync<AiOperationResult>())!;
        var plainText = Fixtures.Result().Exports.Single(export => export.Kind == ExportKind.PlainText).Content;

        Assert.Equal(document.Exports.Single(export => export.Kind == "plaintext").Tokens, answer.BenchmarkTokens);
        Assert.Equal(Encoding.UTF8.GetByteCount(plainText), answer.AiInputBytes);
        Assert.Equal(12, answer.ResponseMilliseconds);
        Assert.Null(answer.ActualInputTokens);
        Assert.Null(answer.ActualOutputTokens);
        Assert.Null(answer.ActualCachedInputTokens);
        Assert.Null(answer.ActualTotalTokens);
        Assert.Equal(1, factory.Processor.Calls);
    }

    [Fact]
    public async Task CompareAllUsesEachCompleteStoredExportSequentiallyAndContinuesAfterAFailure()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var processed = await client.SendAsync(Upload());
        var document = (await processed.Content.ReadFromJsonAsync<ComparisonResult>())!;
        using var saved = await client.PutAsJsonAsync("/api/settings", new { apiKey = "test-secret", model = "test-model" });
        factory.AiProvider.FailedRepresentations.Add(AiRepresentation.DocLang);
        using var compared = await client.PostAsJsonAsync("/api/ai/compare", new { documentId = document.Id, question = "What is here?" });
        compared.EnsureSuccessStatusCode();
        var response = (await compared.Content.ReadFromJsonAsync<AiComparisonResponse>())!;
        Assert.Equal(["json", "doclang", "plaintext"], response.Results.Select(item => item.Representation));
        Assert.Equal([AiRepresentation.Json, AiRepresentation.DocLang, AiRepresentation.PlainText], factory.AiProvider.Requests.Select(item => item.Representation));
        Assert.Equal(Fixtures.Json, factory.AiProvider.Requests[0].Document);
        Assert.Equal(Fixtures.DocLang, factory.AiProvider.Requests[1].Document);
        Assert.Equal(Fixtures.Result().Exports.Single(item => item.Kind == ExportKind.PlainText).Content, factory.AiProvider.Requests[2].Document);
        Assert.All(factory.AiProvider.Requests, item => Assert.Equal("What is here?", item.Question));
        Assert.Equal("failed", response.Results[1].Status);
        Assert.NotNull(response.Results[1].Error);
        Assert.Equal(120, response.Results[0].ActualInputTokens);
        Assert.True(response.Results[0].BenchmarkTokens > 0);
        Assert.Equal(15, response.Results[0].ActualOutputTokens);
        Assert.Equal(135, response.Results[0].ActualTotalTokens);
        Assert.Equal(12, response.Results[0].ResponseMilliseconds);
        Assert.Equal(1, factory.Processor.Calls);
    }

    [Fact]
    public async Task FailedReplacementRetainsPreviousResultAndClearExpiresDownloads()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var first = await client.SendAsync(Upload());
        var result = (await first.Content.ReadFromJsonAsync<ComparisonResult>())!;
        factory.Processor.Fail = true;
        using var failed = await client.SendAsync(Upload());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var current = await client.GetFromJsonAsync<ComparisonResult>("/api/result");
        Assert.Equal(result.Id, current!.Id);
        using var clear = await client.DeleteAsync("/api/result");
        clear.EnsureSuccessStatusCode();
        using var expired = await client.GetAsync("/api/exports/" + result.Id + "/json/download");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task PresentationFeatureChangesDoNotReprocessTheDocument()
    {
        await using var factory = new DemoFactory();
        using var client = factory.DemoClient();
        using var processed = await client.SendAsync(Upload());
        processed.EnsureSuccessStatusCode();
        using var changed = await client.PutAsJsonAsync("/api/settings", new
        {
            demoFeatures = new { showJson = false, showPlainText = false, enableAiTest = false }
        });
        changed.EnsureSuccessStatusCode();
        using var preset = await client.PutAsJsonAsync("/api/settings", new { experiencePreset = "Technical" });
        preset.EnsureSuccessStatusCode();
        Assert.Contains("\"preset\":\"Technical\"", await preset.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Processor.Calls);
        var current = await client.GetFromJsonAsync<ComparisonResult>("/api/result");
        Assert.NotNull(current);
        Assert.Equal(1, factory.Processor.Calls);
        Assert.Empty(factory.AiProvider.Requests);
    }

    private static HttpRequestMessage Upload()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/process") { Content = new ByteArrayContent([1, 2, 3]) };
        request.Headers.Add("X-File-Name", "sample.pdf");
        return request;
    }
}

internal sealed class FakeAiProvider : IAiProvider
{
    public string? DocLang { get; private set; }
    public string? Question { get; private set; }
    public string? AskModel { get; private set; }
    public string? ConnectionModel { get; private set; }
    public DemoException? ConnectionError { get; set; }
    public DemoException? AskError { get; set; }
    public AiUsage? Usage { get; set; } = new(120, 20, 15, 135);
    public long ResponseMilliseconds { get; set; } = 12;
    public HashSet<AiRepresentation> FailedRepresentations { get; } = [];
    public List<AiProviderRequest> Requests { get; } = [];

    public Task<AiConnectionResult> TestConnectionAsync(AiCredentials credentials, CancellationToken cancellationToken)
    {
        if (ConnectionError is not null) throw ConnectionError;
        ConnectionModel = credentials.Model;
        return Task.FromResult(new AiConnectionResult(credentials.Model));
    }

    public Task<AiProviderResult> AskAsync(AiProviderRequest request, AiCredentials credentials, CancellationToken cancellationToken)
    {
        if (AskError is not null) throw AskError;
        Requests.Add(request);
        if (FailedRepresentations.Contains(request.Representation))
            throw new DemoException(request.Representation.Label() + " failed for this test.", 502);
        if (request.Representation == AiRepresentation.DocLang) DocLang = request.Document;
        Question = request.Question;
        AskModel = credentials.Model;
        return Task.FromResult(new AiProviderResult(request.Representation, "Test answer.", credentials.Model, false,
            Usage, ResponseMilliseconds, "completed"));
    }
}
