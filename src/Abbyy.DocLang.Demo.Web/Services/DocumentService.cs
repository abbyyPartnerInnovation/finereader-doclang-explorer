namespace Abbyy.DocLang.Demo;

public sealed class DocumentService(IDocumentProcessor processor, BenchmarkTokenizer tokenizer, ProcessingOptions options)
{
    private readonly SemaphoreSlim processing = new(1, 1);
    private StoredResult? current;
    public StoredResult? Current => Volatile.Read(ref current);
    public StoredResult Get(Guid id) => Current is { } result && result.Summary.Id == id
        ? result : throw new DemoException("This result is no longer available. Process the document again.", 404);
    public void Clear() => Interlocked.Exchange(ref current, null);

    public async Task<StoredResult> ProcessAsync(Stream input, string fileName, CancellationToken cancellationToken)
    {
        var safeName = SafeFiles.DisplayName(fileName);
        var extension = SafeFiles.ValidateExtension(safeName);
        if (!await processing.WaitAsync(0, cancellationToken))
            throw new DemoException("A document is already being processed. Please wait.", 409);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FineReader-DocLang-Demo"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "source" + extension);
            await using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await SafeFiles.CopyLimitedAsync(input, output, options.MaxUploadBytes, cancellationToken);
            var native = await processor.ProcessAsync(source, directory, cancellationToken);
            var summaries = native.Exports.Select(export =>
            {
                var preview = export.Content[..Math.Min(options.PreviewMaxCharacters, export.Content.Length)];
                return new ExportSummary(export.Kind.ToString().ToLowerInvariant(),
                    export.Kind == ExportKind.PlainText ? "Plain Text" : export.Kind == ExportKind.Json ? "JSON" : "DocLang",
                    export.FileName, tokenizer.Count(export.Content), export.Bytes.LongLength,
                    preview, preview.Length < export.Content.Length);
            }).ToArray();
            var json = native.Exports.Single(x => x.Kind == ExportKind.Json);
            var doclang = native.Exports.Single(x => x.Kind == ExportKind.DocLang);
            var result = new StoredResult(new(Guid.NewGuid(), safeName, native.Pages, native.ProcessingSeconds,
                BenchmarkTokenizer.Reduction(summaries.Single(x => x.Kind == "json").Tokens,
                    summaries.Single(x => x.Kind == "doclang").Tokens), summaries,
                JsonMetrics.Extract(json.Content), DocLangMetrics.Extract(doclang.Content)), native.Exports);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref current, result);
            return result;
        }
        finally
        {
            try { SafeFiles.DeleteWorkingDirectory(root, directory); }
            finally { processing.Release(); }
        }
    }
}

public static class SafeFiles
{
    private static readonly HashSet<string> Extensions = [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"];
    public static string DisplayName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new DemoException("Choose a document with a filename of 1–200 characters.");
        return name;
    }
    public static string ValidateExtension(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new DemoException("Choose a PDF, PNG, JPEG, TIFF, or BMP document.");
        return extension;
    }
    public static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > limit) throw new DemoException("The document exceeds the upload size limit.", 413);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new DemoException("The selected document is empty.");
    }
    public static void DeleteWorkingDirectory(string root, string directory)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Refusing to delete a path outside the demo's generated working folders.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
