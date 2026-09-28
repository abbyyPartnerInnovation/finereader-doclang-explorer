using System.Text;

namespace Abbyy.DocLang.Demo;

public enum AiRepresentation { Json, DocLang, PlainText }

public static class AiRepresentationExtensions
{
    public static AiRepresentation Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "doclang" => AiRepresentation.DocLang,
        "json" => AiRepresentation.Json,
        "text" or "plaintext" or "plain-text" => AiRepresentation.PlainText,
        _ => throw new DemoException("Choose JSON, DocLang, or Plain Text as the AI representation.")
    };

    public static string ApiName(this AiRepresentation representation) => representation switch
    {
        AiRepresentation.Json => "json",
        AiRepresentation.DocLang => "doclang",
        AiRepresentation.PlainText => "plaintext",
        _ => throw new ArgumentOutOfRangeException(nameof(representation))
    };

    public static string Label(this AiRepresentation representation) => representation switch
    {
        AiRepresentation.Json => "JSON",
        AiRepresentation.DocLang => "DocLang",
        AiRepresentation.PlainText => "Plain Text",
        _ => throw new ArgumentOutOfRangeException(nameof(representation))
    };

    public static string ExportKind(this AiRepresentation representation) => representation switch
    {
        AiRepresentation.Json => "json",
        AiRepresentation.DocLang => "doclang",
        AiRepresentation.PlainText => "plaintext",
        _ => throw new ArgumentOutOfRangeException(nameof(representation))
    };
}

public sealed record AiQuestion(Guid DocumentId, string Question, string? Representation = null);
public sealed record AiComparisonQuestion(Guid DocumentId, string Question);
public sealed record AiComparisonResponse(IReadOnlyList<AiOperationResult> Results);

// BenchmarkTokens are local o200k_base counts. Actual* values are reported by the AI provider only.
public sealed record AiOperationResult(
    Guid DocumentId,
    string Representation,
    string Label,
    long AiInputBytes,
    int BenchmarkTokens,
    string? Text,
    string? Model,
    bool Incomplete,
    int? ActualInputTokens,
    int? ActualCachedInputTokens,
    int? ActualOutputTokens,
    int? ActualTotalTokens,
    long? ResponseMilliseconds,
    string Status,
    string? Error);

public sealed class AiComparisonService(
    DocumentService documents,
    SessionSettings settings,
    IAiProvider provider)
{
    private static readonly AiRepresentation[] ComparisonOrder =
        [AiRepresentation.Json, AiRepresentation.DocLang, AiRepresentation.PlainText];

    public async Task<AiOperationResult> AskAsync(AiQuestion input, CancellationToken cancellationToken)
    {
        var representation = AiRepresentationExtensions.Parse(input.Representation);
        ValidateQuestion(input.Question);
        var document = documents.Get(input.DocumentId);
        return await AskOneAsync(document, representation, input.Question.Trim(), settings.GetCredentials(), cancellationToken);
    }

    public async Task<AiComparisonResponse> CompareAsync(AiComparisonQuestion input, CancellationToken cancellationToken)
    {
        ValidateQuestion(input.Question);
        var document = documents.Get(input.DocumentId);
        var credentials = settings.GetCredentials();
        var results = new List<AiOperationResult>(ComparisonOrder.Length);

        // Sequential by design: model, question, instructions and settings are equal for every request.
        foreach (var representation in ComparisonOrder)
        {
            try
            {
                results.Add(await AskOneAsync(document, representation, input.Question.Trim(), credentials, cancellationToken));
            }
            catch (DemoException exception)
            {
                results.Add(FailedResult(document, representation, credentials.Model, exception.Message));
            }
        }
        return new(results);
    }

    private async Task<AiOperationResult> AskOneAsync(
        StoredResult document,
        AiRepresentation representation,
        string question,
        AiCredentials credentials,
        CancellationToken cancellationToken)
    {
        var export = document.GetExport(representation.ExportKind());
        var benchmarkTokens = BenchmarkTokens(document, representation);
        // This is the UTF-8 size of the exact complete string passed to IAiProvider below.
        var aiInputBytes = Encoding.UTF8.GetByteCount(export.Content);
        var response = await provider.AskAsync(new(representation, export.Content, question), credentials, cancellationToken);
        var usage = response.Usage;
        return new(
            document.Summary.Id, representation.ApiName(), representation.Label(), aiInputBytes, benchmarkTokens, response.Text, response.Model,
            response.Incomplete, usage?.InputTokens, usage?.CachedInputTokens, usage?.OutputTokens, usage?.TotalTokens,
            response.ResponseMilliseconds, response.Status, null);
    }

    private static AiOperationResult FailedResult(StoredResult document, AiRepresentation representation, string model, string error) =>
        new(document.Summary.Id, representation.ApiName(), representation.Label(), AiInputBytes(document, representation), BenchmarkTokens(document, representation), null, model, false,
            null, null, null, null, null, "failed", error);

    private static int BenchmarkTokens(StoredResult document, AiRepresentation representation) => document.Summary.Exports.Single(summary =>
        string.Equals(summary.Kind, representation.ExportKind(), StringComparison.OrdinalIgnoreCase)).Tokens;

    private static long AiInputBytes(StoredResult document, AiRepresentation representation) =>
        Encoding.UTF8.GetByteCount(document.GetExport(representation.ExportKind()).Content);

    private static void ValidateQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000)
            throw new DemoException("Enter a question of 1-4,000 characters.");
    }
}
