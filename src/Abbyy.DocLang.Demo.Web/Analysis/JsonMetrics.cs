using System.Globalization;
using System.Text.Json;

namespace Abbyy.DocLang.Demo;

public static class JsonMetrics
{
    // FRE native layout only: content also contains logical table references, which must not be double-counted.
    public static Analysis Extract(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object
                || !layout.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
                return new([], "The native JSON layout schema was not recognized; counts are unavailable.");
            var counts = new Dictionary<string, long>();
            long suspiciousTrue = 0;
            void Add(string key, long count = 1) => counts[key] = counts.GetValueOrDefault(key) + count;
            void Visit(JsonElement node, string? collection = null)
            {
                if (node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in node.EnumerateArray()) Visit(item, collection);
                    return;
                }
                if (node.ValueKind != JsonValueKind.Object) return;
                foreach (var field in node.EnumerateObject())
                {
                    var value = field.Value;
                    if (value.ValueKind == JsonValueKind.Array &&
                        (field.Name is "texts" or "lines" or "words" or "chars" or "tables"
                         || field.Name == "cells" && collection == "tables"))
                        Add(field.Name, value.EnumerateArray().LongCount(x => x.ValueKind == JsonValueKind.Object));
                    if (field.Name is "position" or "colRowPosition" && value.ValueKind == JsonValueKind.Object)
                        Add("geometry");
                    if (field.Name is "confidence" or "errorProbability" && value.ValueKind == JsonValueKind.Number)
                        Add(field.Name);
                    if (field.Name == "suspicious" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        Add("suspicious");
                        if (value.GetBoolean()) suspiciousTrue++;
                    }
                    Visit(value, field.Name);
                }
            }
            Visit(pages, "pages");
            Add("pages", pages.EnumerateArray().LongCount(x => x.ValueKind == JsonValueKind.Object));
            (string Key, string Label)[] labels =
            [
                ("pages", "Pages"), ("texts", "Text regions"), ("lines", "Lines"), ("words", "Words"),
                ("chars", "Character records"), ("geometry", "Position / geometry objects"),
                ("confidence", "Confidence values"), ("errorProbability", "errorProbability values"),
                ("suspicious", "Suspicious flags"), ("tables", "Tables"), ("cells", "Table cells")
            ];
            return new(labels.Select(x => new Fact(x.Label,
                x.Key == "suspicious" ? $"{Number(counts.GetValueOrDefault(x.Key))} stored ({Number(suspiciousTrue)} true)"
                    : Number(counts.GetValueOrDefault(x.Key)))).ToArray());
        }
        catch (JsonException) { return new([], "JSON metrics unavailable: the export could not be parsed."); }
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
