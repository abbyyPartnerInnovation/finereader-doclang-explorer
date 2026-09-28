using System.Text;
using System.Text.Json.Serialization;

namespace Abbyy.DocLang.Demo;

public sealed class FineReaderOptions
{
    public string RuntimePath { get; set; } = "";
    public string CustomerProjectId { get; set; } = "";
    public string LicensePath { get; set; } = "";
    public string LicensePassword { get; set; } = "";
    public string FREngineDataFolder { get; set; } = "";
    public string FREngineTempFolder { get; set; } = "";
    public bool IsSharedCPUCoresMode { get; set; }
    public string ProcessingProfile { get; set; } = "DocumentConversion_Accuracy";
    public string[] RecognitionLanguages { get; set; } = ["English"];
}

public sealed class ProcessingOptions
{
    public int MaxUploadSizeMb { get; set; } = 50;
    public int MaxExportSizeMb { get; set; } = 100;
    public int PreviewMaxCharacters { get; set; } = 18000;
    public long MaxUploadBytes => MaxUploadSizeMb * 1024L * 1024;
    public long MaxExportBytes => MaxExportSizeMb * 1024L * 1024;
    public void Validate()
    {
        if (MaxUploadSizeMb is < 1 or > 200 || MaxExportSizeMb is < 1 or > 500
            || PreviewMaxCharacters is < 1000 or > 100000)
            throw new InvalidOperationException("Invalid Processing limits in appsettings.Local.json.");
    }
}

public sealed class DemoException(string message, int statusCode = 400, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

public enum ExportKind { Json, DocLang, PlainText }

public sealed class NativeExport(ExportKind kind, byte[] bytes)
{
    public ExportKind Kind { get; } = kind;
    public byte[] Bytes { get; } = bytes;
    public string Content { get; } = Decode(bytes);
    public string FileName => Kind switch
    {
        ExportKind.Json => "result.json",
        ExportKind.DocLang => "result.doclang.xml",
        _ => "result.txt"
    };
    public static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

public sealed record NativeResult(int Pages, double ProcessingSeconds, IReadOnlyList<NativeExport> Exports);
public sealed record Fact(string Label, string Value);
public sealed record Analysis(IReadOnlyList<Fact> Facts, string? Unavailable = null);
public sealed record ExportSummary(string Kind, string Label, string FileName, int Tokens, long FileBytes,
    string Preview, bool PreviewTruncated);
public sealed record ComparisonResult(Guid Id, string FileName, int Pages, double ProcessingSeconds,
    double? ReductionPercent, IReadOnlyList<ExportSummary> Exports, Analysis JsonDetails, Analysis DocLangDetails);
public sealed record StoredResult(ComparisonResult Summary, [property: JsonIgnore] IReadOnlyList<NativeExport> Exports)
{
    public NativeExport GetExport(string kind) => Exports.FirstOrDefault(e =>
        string.Equals(e.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
        ?? throw new DemoException("Export not found.", 404);
}
