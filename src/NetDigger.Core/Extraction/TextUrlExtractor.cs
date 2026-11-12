using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetDigger.Core.Extraction;

/// <summary>Pulls URL-looking strings out of CSS, JavaScript, JSON and other text.</summary>
public static partial class TextUrlExtractor
{
    private const int MaxResults = 2000;

    [GeneratedRegex(@"https?://[^\s""'<>\\)\]\}`|^]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUrlRegex();

    [GeneratedRegex(@"url\(\s*(?<q>['""]?)(?<u>[^'"")]+)\k<q>\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"@import\s+(?:url\()?\s*['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImportRegex();

    /// <summary>Finds absolute http(s) URLs in arbitrary text (JavaScript, inline scripts, plain text).</summary>
    public static IEnumerable<string> FindAbsoluteUrls(string text)
    {
        // JSON and JS often escape slashes: https:\/\/example.com
        text = text.Replace("\\/", "/", StringComparison.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AbsoluteUrlRegex().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (url.Length > 8 && seen.Add(url))
            {
                yield return url;
                if (seen.Count >= MaxResults) yield break;
            }
        }
    }

    /// <summary>Finds url(...) and @import references in CSS. Values may be relative.</summary>
    public static IEnumerable<string> FindCssUrls(string css)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var regex in new[] { CssUrlRegex(), CssImportRegex() })
        {
            foreach (Match match in regex.Matches(css))
            {
                var value = match.Groups["u"].Value.Trim();
                if (value.Length == 0 || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(value))
                {
                    yield return value;
                    if (seen.Count >= MaxResults) yield break;
                }
            }
        }
    }
}

/// <summary>Finds URLs inside JSON documents.</summary>
public static class JsonLinkExtractor
{
    private const int MaxResults = 2000;

    /// <summary>Returns every string value that is an absolute http(s) URL. Invalid JSON yields nothing.</summary>
    public static IReadOnlyList<string> FindUrls(string json)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
        }
        catch (JsonException)
        {
            return results;
        }

        using (document)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(document.RootElement);

            while (stack.Count > 0 && results.Count < MaxResults)
            {
                var element = stack.Pop();
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        var value = element.GetString()?.Trim();
                        if (value is not null
                            && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            && !value.Contains(' ')
                            && seen.Add(value))
                        {
                            results.Add(value);
                        }

                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) stack.Push(item);
                        break;
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject()) stack.Push(property.Value);
                        break;
                }
            }
        }

        return results;
    }
}
