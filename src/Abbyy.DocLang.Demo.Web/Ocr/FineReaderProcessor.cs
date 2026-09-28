using System.Diagnostics;

namespace Abbyy.DocLang.Demo;

public interface IDocumentProcessor
{
    Task<NativeResult> ProcessAsync(string source, string directory, CancellationToken cancellationToken);
}

public sealed class FineReaderProcessor(FineReaderOptions options, ProcessingOptions limits)
    : IDocumentProcessor, IAsyncDisposable
{
    private readonly StaWorker worker = new();
    private FREngine.IEngine? engine; // All use and disposal stay on the same STA worker.

    public Task<NativeResult> ProcessAsync(string source, string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CustomerProjectId))
            throw new DemoException("Set FineReader.CustomerProjectId in appsettings.Local.json, then restart.", 503);
        return worker.InvokeAsync(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Initialize();
                return SinglePassExporter.Run(() => new Session(engine!, options, source),
                    directory, limits.MaxExportBytes, cancellationToken);
            }
            catch (DemoException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // Native exception messages can contain licence details. Never return or log them.
                throw new DemoException(exception.HResult == unchecked((int)0x80040112)
                    ? "FineReader licence verification failed. Check the licence configuration and restart."
                    : "FineReader could not process or export this document. Check the 12.8.2 runtime, licence, languages, and input file.", 503);
            }
        });
    }

    private void Initialize()
    {
        if (engine is not null) return;
        var dll = Directory.Exists(options.RuntimePath) ? Path.Combine(options.RuntimePath, "FREngine.dll") : options.RuntimePath;
        if (!Path.IsPathFullyQualified(dll) || !File.Exists(dll))
            throw new DemoException("Set FineReader.RuntimePath to your installed Bin64 directory or FREngine.dll, then restart.", 503);
        var info = FileVersionInfo.GetVersionInfo(dll);
        var runtimeVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (runtimeVersion != typeof(FREngine.FREngine).Assembly.GetName().Version
            || runtimeVersion < new Version(12, 8, 2, 0))
            throw new DemoException("FineReader runtime and .NET wrapper must match (12.8.2 or later). Run tools/Setup-FineReader.ps1 and rebuild.", 503);
        FREngine.FREngine.SetFREnginePath(Path.GetDirectoryName(dll)!);
        engine = FREngine.FREngine.InitializeEngine(options.CustomerProjectId, options.LicensePath,
            options.LicensePassword, options.FREngineDataFolder, options.FREngineTempFolder, options.IsSharedCPUCoresMode);
        try
        {
            if (engine is null) throw new InvalidOperationException();
            engine.LoadPredefinedProfile(options.ProcessingProfile);
        }
        catch { CloseEngine(); throw; }
    }

    private void CloseEngine()
    {
        var current = engine;
        engine = null;
        if (current is null) return;
        try { current.Dispose(); }
        finally { FREngine.FREngine.DeinitializeEngine(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await worker.InvokeAsync(() => { CloseEngine(); return true; }); }
        finally { await worker.DisposeAsync(); }
    }

    private sealed class Session : IRecognitionSession
    {
        private readonly FREngine.IEngine engine;
        private readonly FineReaderOptions options;
        private readonly FREngine.FRDocument document;
        private bool recognized;
        public Session(FREngine.IEngine engine, FineReaderOptions options, string source)
        {
            this.engine = engine;
            this.options = options;
            document = engine.CreateFRDocument();
            try { document.AddImageFile(source, null, null); }
            catch { Dispose(); throw; }
        }
        public void Recognize()
        {
            if (recognized) throw new InvalidOperationException("Recognition already performed.");
            recognized = true;
            using var processing = engine.CreateDocumentProcessingParams();
            using var page = processing.PageProcessingParams;
            using var recognizer = page.RecognizerParams;
            recognizer.SetPredefinedTextLanguage(string.Join(",", options.RecognitionLanguages));
            document.Process(processing);
        }
        public int PageCount { get { using var pages = document.Pages; return pages.Count; } }
        public void Export(ExportKind kind, string path)
        {
            if (!recognized) throw new InvalidOperationException("Recognition must precede export.");
            var format = kind switch
            {
                ExportKind.Json => FREngine.FileExportFormatEnum.FEF_JSON,
                ExportKind.DocLang => FREngine.FileExportFormatEnum.FEF_DocLang,
                _ => FREngine.FileExportFormatEnum.FEF_TextUnicodeDefaults
            };
            document.Export(path, format, null);
        }
        public void Dispose()
        {
            try { document.Close(); }
            finally { document.Dispose(); }
        }
    }
}
