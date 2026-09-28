using System.Text;
using Microsoft.ML.Tokenizers;

namespace Abbyy.DocLang.Demo.Tests;

public sealed class AnalysisTests
{
    [Fact]
    public void BenchmarkUsesExactO200kEncodingAndCompleteNativeExports()
    {
        var benchmark = new BenchmarkTokenizer();
        var reference = TiktokenTokenizer.CreateForEncoding("o200k_base");
        Assert.Equal("o200k_base", BenchmarkTokenizer.Encoding);
        Assert.Equal(0, benchmark.Count(""));
        Assert.Equal(4, benchmark.Count("Hello, world!"));
        var longContent = Fixtures.Json + string.Concat(Enumerable.Repeat("\r\n مرحباً — long document", 10000));
        Assert.Equal(reference.CountTokens(longContent), benchmark.Count(longContent));
        Assert.NotEqual(benchmark.Count(longContent[..18000]), benchmark.Count(longContent));
        Assert.NotEqual(benchmark.Count("{\"a\":1}"), benchmark.Count("{\n  \"a\": 1\n}"));
    }

    [Theory]
    [InlineData(100, 25, 75)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 150, -50)]
    [InlineData(100, 0, 100)]
    public void ReductionIsRelativeToJsonAndMayBeNegative(int json, int doclang, double expected) =>
        Assert.Equal(expected, BenchmarkTokenizer.Reduction(json, doclang));

    [Fact]
    public void EmptyJsonHasNoPercentage() => Assert.Null(BenchmarkTokenizer.Reduction(0, 10));

    [Fact]
    public void NativeByteSizeAndBomDecodingAreIndependentOfTokenCount()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("A\r\nمرحبا")).ToArray();
        var export = new NativeExport(ExportKind.PlainText, bytes);
        Assert.Equal("A\r\nمرحبا", export.Content);
        Assert.Equal(bytes.Length, export.Bytes.Length);
        Assert.DoesNotContain('\uFEFF', export.Content);
    }

    [Fact]
    public void JsonCountsNativeLayoutWithoutLogicalContentDuplicates()
    {
        var analysis = JsonMetrics.Extract(Fixtures.Json);
        Assert.Null(analysis.Unavailable);
        var facts = analysis.Facts.ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal(11, facts.Count);
        Assert.Equal("2", facts["Pages"]);
        Assert.Equal("2", facts["Text regions"]);
        Assert.Equal("1", facts["Lines"]);
        Assert.Equal("1", facts["Words"]);
        Assert.Equal("2", facts["Character records"]);
        Assert.Equal("7", facts["Position / geometry objects"]);
        Assert.Equal("4", facts["Confidence values"]);
        Assert.Equal("2", facts["errorProbability values"]);
        Assert.Equal("2 stored (1 true)", facts["Suspicious flags"]);
        Assert.Equal("1", facts["Tables"]);
        Assert.Equal("2", facts["Table cells"]);
    }

    [Fact]
    public void JsonIgnoresNonrecordArrayEntriesAndReportsAbsentFieldsAsZero()
    {
        var facts = JsonMetrics.Extract("""{"layout":{"pages":[{"texts":[null,1,"x"],"confidence":"not a number","suspicious":1},null]}}""")
            .Facts.ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal("1", facts["Pages"]);
        Assert.Equal("0", facts["Text regions"]);
        Assert.Equal("0", facts["Confidence values"]);
        Assert.Equal("0 stored (0 true)", facts["Suspicious flags"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"pages":[{}]}""")]
    public void UnsupportedJsonReturnsNoInventedCounts(string input)
    {
        var analysis = JsonMetrics.Extract(input);
        Assert.NotNull(analysis.Unavailable);
        Assert.Empty(analysis.Facts);
    }

    [Fact]
    public void DocLangReportsOnlyExplicitNativeElements()
    {
        var analysis = DocLangMetrics.Extract(Fixtures.DocLang);
        var facts = analysis.Facts.ToDictionary(x => x.Label, x => x.Value);
        Assert.Null(analysis.Unavailable);
        Assert.Equal("0.4", facts["Specification version"]);
        Assert.Equal("1", facts["Page boundaries"]);
        Assert.Equal("1", facts["Tables"]);
        Assert.Equal("2", facts["Location elements"]);
        Assert.Equal("4", facts["Structural blocks"]);
        Assert.DoesNotContain(facts.Keys, x => x.Contains("language", StringComparison.OrdinalIgnoreCase) || x == "Pages");
    }

    [Fact]
    public void EmptyDocLangDoesNotInventVersionOrPages()
    {
        var facts = DocLangMetrics.Extract("""<doclang xmlns="https://www.doclang.ai/ns/v0"/>""").Facts;
        Assert.DoesNotContain(facts, x => x.Label is "Specification version" or "Pages");
        Assert.All(facts, fact => Assert.Equal("0", fact.Value));
    }

    [Theory]
    [InlineData("<doclang><table/></doclang>")]
    [InlineData("<doclang")]
    [InlineData("""<!DOCTYPE doclang [<!ENTITY attack SYSTEM "file:///C:/Windows/win.ini">]><doclang xmlns="https://www.doclang.ai/ns/v0">&attack;</doclang>""")]
    public void UnsupportedOrUnsafeDocLangReturnsNoFacts(string input)
    {
        var analysis = DocLangMetrics.Extract(input);
        Assert.NotNull(analysis.Unavailable);
        Assert.Empty(analysis.Facts);
    }
}
