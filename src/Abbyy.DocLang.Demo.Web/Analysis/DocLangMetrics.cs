using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Abbyy.DocLang.Demo;

public static class DocLangMetrics
{
    public const string Namespace = "https://www.doclang.ai/ns/v0";
    private static readonly HashSet<string> Blocks = ["text", "heading", "table", "picture", "list_item",
        "caption", "footnote", "page_header", "page_footer", "formula", "code"];

    public static Analysis Extract(string content)
    {
        try
        {
            using var input = new StringReader(content);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 150_000_000
            });
            var document = XDocument.Load(reader);
            XNamespace ns = Namespace;
            if (document.Root is not { } root || root.Name != ns + "doclang")
                return new([], "This DocLang namespace is not supported by the structure summary.");
            var elements = new List<XElement>();
            var pending = new Stack<XElement>(root.Elements().Reverse());
            while (pending.TryPop(out var element))
            {
                // Ignore metadata and entire foreign subtrees, even if they contain native-looking child names.
                if (element.Name.Namespace != ns || element.Name.LocalName == "head") continue;
                elements.Add(element);
                foreach (var child in element.Elements().Reverse()) pending.Push(child);
            }
            var facts = new List<Fact>();
            if (root.Attribute("version")?.Value is { Length: > 0 } version)
                facts.Add(new("Specification version", version));
            void Count(string label, int count) => facts.Add(new(label, count.ToString("N0", CultureInfo.InvariantCulture)));
            Count("Page boundaries", elements.Count(x => x.Name.LocalName == "page_break"));
            // A page_break is observable; a page total for empty/terminal pages is not inferred.
            Count("Tables", elements.Count(x => x.Name.LocalName == "table"));
            Count("Location elements", elements.Count(x => x.Name.LocalName == "location"));
            Count("Structural blocks", elements.Count(x => Blocks.Contains(x.Name.LocalName)));
            return new(facts);
        }
        catch (XmlException) { return new([], "DocLang metrics unavailable: the export could not be parsed safely."); }
    }
}
