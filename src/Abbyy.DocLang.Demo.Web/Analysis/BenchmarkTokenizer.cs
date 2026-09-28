using Microsoft.ML.Tokenizers;

namespace Abbyy.DocLang.Demo;

public sealed class BenchmarkTokenizer
{
    public const string Encoding = "o200k_base";
    private readonly Lazy<TiktokenTokenizer> tokenizer = new(() => TiktokenTokenizer.CreateForEncoding(Encoding));
    private readonly object sync = new();

    public int Count(string completeExport)
    {
        lock (sync) return tokenizer.Value.CountTokens(completeExport);
    }

    public static double? Reduction(int jsonTokens, int docLangTokens) =>
        jsonTokens > 0 ? 100d * (jsonTokens - (double)docLangTokens) / jsonTokens : null;
}
