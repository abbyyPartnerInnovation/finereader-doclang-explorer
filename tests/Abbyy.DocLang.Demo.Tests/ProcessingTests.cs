using System.Text;

namespace Abbyy.DocLang.Demo.Tests;

public sealed class ProcessingTests
{
    [Fact]
    public void OneNativeSessionRecognizesExactlyOnceBeforeAllExports()
    {
        var root = Path.Combine(Path.GetTempPath(), "FineReader-DocLang-Demo");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = new RecordingSession();
        try
        {
            var result = SinglePassExporter.Run(() => session, directory, 1_000_000, CancellationToken.None);
            Assert.Equal(["recognize", "Json", "DocLang", "PlainText", "dispose"], session.Calls);
            Assert.Equal(3, result.Exports.Count);
            Assert.Equal(2, result.Pages);
        }
        finally { SafeFiles.DeleteWorkingDirectory(root, directory); }
    }

    [Fact]
    public void ExportFailureClosesSessionWithoutRerecognizing()
    {
        var root = Path.Combine(Path.GetTempPath(), "FineReader-DocLang-Demo");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = new RecordingSession { FailOnDocLang = true };
        try
        {
            Assert.Throws<InvalidOperationException>(() => SinglePassExporter.Run(() => session, directory, 1_000_000, CancellationToken.None));
            Assert.Equal(["recognize", "Json", "DocLang", "dispose"], session.Calls);
        }
        finally { SafeFiles.DeleteWorkingDirectory(root, directory); }
    }

    [Fact]
    public async Task DocumentServiceCleansFilesAndUsesCompleteExportsForCounts()
    {
        var processor = new FakeProcessor();
        var options = new ProcessingOptions { PreviewMaxCharacters = 8 };
        var tokenizer = new BenchmarkTokenizer();
        var documents = new DocumentService(processor, tokenizer, options);
        using var input = new MemoryStream([1, 2, 3]);
        var result = await documents.ProcessAsync(input, "../../sample.pdf", CancellationToken.None);
        Assert.Equal("sample.pdf", result.Summary.FileName);
        Assert.Equal("source.pdf", Path.GetFileName(processor.Source));
        Assert.False(Directory.Exists(processor.Directory));
        Assert.Equal(1, processor.Calls);
        var json = result.Summary.Exports.Single(x => x.Kind == "json");
        Assert.True(json.PreviewTruncated);
        Assert.Equal(8, json.Preview.Length);
        Assert.Equal(tokenizer.Count(Fixtures.Json), json.Tokens);
        Assert.Equal(Encoding.UTF8.GetByteCount(Fixtures.Json), json.FileBytes);
        Assert.Equal(Fixtures.DocLang, documents.Get(result.Summary.Id).GetExport("doclang").Content);
    }

    [Fact]
    public async Task ParallelRequestsCannotDuplicateOcrAndFailuresAreCleaned()
    {
        var processor = new FakeProcessor { Block = new(TaskCreationOptions.RunContinuationsAsynchronously), Fail = true };
        var documents = new DocumentService(processor, new BenchmarkTokenizer(), new ProcessingOptions());
        using var input = new MemoryStream([1, 2, 3]);
        var first = documents.ProcessAsync(input, "one.pdf", CancellationToken.None);
        for (var attempt = 0; processor.Calls == 0 && attempt < 100; attempt++) await Task.Delay(10);
        Assert.Equal(1, processor.Calls);
        using var duplicate = new MemoryStream([1, 2, 3]);
        var exception = await Assert.ThrowsAsync<DemoException>(() => documents.ProcessAsync(duplicate, "two.pdf", CancellationToken.None));
        Assert.Equal(409, exception.StatusCode);
        processor.Block.SetResult();
        await Assert.ThrowsAsync<DemoException>(() => first);
        Assert.False(Directory.Exists(processor.Directory));
        Assert.Null(documents.Current);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task UploadLimitDoesNotTrustDeclaredContentLength()
    {
        using var input = new MemoryStream(new byte[100]);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DemoException>(() =>
            SafeFiles.CopyLimitedAsync(input, output, 50, CancellationToken.None));
        Assert.Equal(413, exception.StatusCode);
    }

    [Fact]
    public void CleanupCannotEscapeItsGeneratedDirectory() =>
        Assert.Throws<InvalidOperationException>(() => SafeFiles.DeleteWorkingDirectory(
            Path.Combine(Path.GetTempPath(), "FineReader-DocLang-Demo"), Path.GetTempPath()));

    private sealed class RecordingSession : IRecognitionSession
    {
        public List<string> Calls { get; } = [];
        public bool FailOnDocLang;
        public int PageCount => 2;
        public void Recognize() => Calls.Add("recognize");
        public void Export(ExportKind kind, string path)
        {
            Calls.Add(kind.ToString());
            if (FailOnDocLang && kind == ExportKind.DocLang) throw new InvalidOperationException();
            File.WriteAllText(path, "native fixture");
        }
        public void Dispose() => Calls.Add("dispose");
    }
}
