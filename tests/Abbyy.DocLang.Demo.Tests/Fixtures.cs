using System.Text;
using Abbyy.DocLang.Demo;

namespace Abbyy.DocLang.Demo.Tests;

internal static class Fixtures
{
    public const string Json = """
    {
      "version":"FineReader Engine 12",
      "layout":{"pages":[
        {"texts":[{"position":{"l":1},"confidence":99,"lines":[
          {"position":{"l":2},"confidence":98,"words":[
            {"confidence":97,"chars":[
              {"text":"A","position":{"l":3},"confidence":96,"errorProbability":0,"suspicious":false},
              {"text":"B","position":{"l":4},"errorProbability":2,"suspicious":true}
            ]}
          ]}
        ]}],
        "tables":[{"position":{"l":5},"cells":[
          {"colRowPosition":{"row":0},"texts":[{"lines":[]}]},
          {"colRowPosition":{"row":0}}
        ]}]},
        {"texts":[]}
      ]},
      "content":{"tables":[{"cells":[{}]}],"texts":[{"words":[{}]}]}
    }
    """;
    public const string DocLang = """
    <?xml version="1.0" encoding="utf-8"?>
    <doclang xmlns="https://www.doclang.ai/ns/v0" version="0.4">
      <head><generated_by>Test fixture</generated_by><table/><location/></head>
      <text><location value="10"/>Hello, world! مرحباً</text>
      <table><location value="20"/><fcel/>Value<nl/></table>
      <page_break/>
      <heading>Second page</heading><text>Goodbye.</text>
      <foreign xmlns="urn:foreign"><table xmlns="https://www.doclang.ai/ns/v0"/></foreign>
    </doclang>
    """;
    public static NativeResult Result() => new(2, 1.25,
    [
        new(ExportKind.Json, Encoding.UTF8.GetBytes(Json)),
        new(ExportKind.DocLang, Encoding.UTF8.GetBytes(DocLang)),
        new(ExportKind.PlainText, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Hello, world! مرحباً\r\nGoodbye.")).ToArray())
    ]);
}

internal sealed class FakeProcessor : IDocumentProcessor
{
    public int Calls;
    public string? Source;
    public string? Directory;
    public TaskCompletionSource? Block;
    public bool Fail;
    public async Task<NativeResult> ProcessAsync(string source, string directory, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Source = source;
        Directory = directory;
        if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
        if (Fail) throw new DemoException("Simulated OCR failure.", 503);
        return Fixtures.Result();
    }
}
