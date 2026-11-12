using System.Xml;
using System.Xml.Linq;

namespace NetDigger.Core.Extraction;

public enum XmlDocumentKind
{
    Sitemap,
    SitemapIndex,
    Other
}

public sealed record XmlExtraction(XmlDocumentKind Kind, IReadOnlyList<string> Urls);

/// <summary>Extracts URLs from sitemaps, feeds and arbitrary XML. DTDs and external entities are never processed.</summary>
public static class XmlLinkExtractor
{
    private const int MaxResults = 100_000;

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "url", "link"
    };

    public static XmlExtraction Extract(string xml)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 50_000_000
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return new XmlExtraction(XmlDocumentKind.Other, Array.Empty<string>());
        }

        var root = document.Root;
        if (root is null) return new XmlExtraction(XmlDocumentKind.Other, Array.Empty<string>());

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? candidate)
        {
            if (urls.Count >= MaxResults) return;
            candidate = candidate?.Trim();
            if (string.IsNullOrEmpty(candidate)) return;
            if (seen.Add(candidate)) urls.Add(candidate);
        }

        var rootName = root.Name.LocalName;
        if (rootName is "urlset" or "sitemapindex")
        {
            foreach (var loc in root.Descendants().Where(e => e.Name.LocalName == "loc"))
                Add(loc.Value);

            return new XmlExtraction(rootName == "urlset" ? XmlDocumentKind.Sitemap : XmlDocumentKind.SitemapIndex, urls);
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (UrlAttributes.Contains(attribute.Name.LocalName) && LooksLikeUrl(attribute.Value))
                    Add(attribute.Value);
            }

            if (!element.HasElements && LooksLikeUrl(element.Value))
                Add(element.Value);
        }

        return new XmlExtraction(XmlDocumentKind.Other, urls);
    }

    private static bool LooksLikeUrl(string value)
    {
        value = value.Trim();
        return value.Length > 8 && !value.Contains(' ')
               && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
