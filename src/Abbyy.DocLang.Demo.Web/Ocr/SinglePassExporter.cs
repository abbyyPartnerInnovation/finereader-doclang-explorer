using System.Diagnostics;

namespace Abbyy.DocLang.Demo;

public interface IRecognitionSession : IDisposable
{
    void Recognize();
    int PageCount { get; }
    void Export(ExportKind kind, string path);
}

public static class SinglePassExporter
{
    public static NativeResult Run(Func<IRecognitionSession> open, string workingDirectory,
        long maxExportBytes, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var document = open();
        cancellationToken.ThrowIfCancellationRequested();
        document.Recognize(); // The only recognition call; exports never invoke recognition.
        var pages = document.PageCount;
        var paths = new List<(ExportKind Kind, string Path)>();
        foreach (var kind in Enum.GetValues<ExportKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(workingDirectory, kind switch
            {
                ExportKind.Json => "result.json",
                ExportKind.DocLang => "result.doclang.xml",
                _ => "result.txt"
            });
            document.Export(kind, path);
            if (new FileInfo(path).Length > maxExportBytes)
                throw new DemoException("An export exceeds the configured size limit. Use a smaller document.", 413);
            paths.Add((kind, path));
        }
        watch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        return new(pages, watch.Elapsed.TotalSeconds,
            paths.Select(x => new NativeExport(x.Kind, File.ReadAllBytes(x.Path))).ToArray());
    }
}
