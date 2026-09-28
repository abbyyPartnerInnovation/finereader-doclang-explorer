using System.Text;

namespace Abbyy.DocLang.Demo.Tests;

public sealed class AiComparisonTests
{
    [Fact]
    public async Task ComparisonUsesStoredFullExportsInOrderWithProviderUsageAndDuration()
    {
        var processor = new FakeProcessor();
        var documents = new DocumentService(processor, new BenchmarkTokenizer(), new ProcessingOptions());
        using var input = new MemoryStream([1, 2, 3]);
        var stored = await documents.ProcessAsync(input, "sample.pdf", CancellationToken.None);
        var settings = new SessionSettings();
        settings.Update(new() { ApiKey = "test-secret", Model = "test-model" });
        var provider = new FakeAiProvider();
        var service = new AiComparisonService(documents, settings, provider);

        var comparison = await service.CompareAsync(new(stored.Summary.Id, "What is here?"), CancellationToken.None);

        Assert.Equal(["json", "doclang", "plaintext"], comparison.Results.Select(item => item.Representation));
        Assert.Equal([Fixtures.Json, Fixtures.DocLang, Fixtures.Result().Exports.Single(item => item.Kind == ExportKind.PlainText).Content],
            provider.Requests.Select(request => request.Document));
        Assert.All(provider.Requests, request => Assert.Equal("What is here?", request.Question));
        Assert.All(comparison.Results, item =>
        {
            Assert.True(item.BenchmarkTokens > 0);
            Assert.Equal(120, item.ActualInputTokens);
            Assert.Equal(20, item.ActualCachedInputTokens);
            Assert.Equal(15, item.ActualOutputTokens);
            Assert.Equal(135, item.ActualTotalTokens);
            Assert.Equal(12, item.ResponseMilliseconds);
        });
        for (var index = 0; index < comparison.Results.Count; index++)
        {
            var operation = comparison.Results[index];
            var request = provider.Requests[index];
            var storedSummary = stored.Summary.Exports.Single(summary => summary.Kind == operation.Representation);
            Assert.Equal(storedSummary.Tokens, operation.BenchmarkTokens);
            Assert.Equal(Encoding.UTF8.GetByteCount(request.Document), operation.AiInputBytes);
        }
        Assert.Equal(1, processor.Calls);
    }
}
