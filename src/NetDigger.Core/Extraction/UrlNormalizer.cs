using System.Diagnostics.CodeAnalysis;

namespace NetDigger.Core.Extraction;

/// <summary>
/// Turns raw href/src values into canonical absolute http(s) URLs so that the same resource
/// is never visited twice under different spellings.
/// </summary>
public static class UrlNormalizer
{
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "yclid", "mc_cid", "mc_eid", "igshid", "_ga", "_gl"
    };

    /// <summary>
    /// Resolves <paramref name="raw"/> against <paramref name="baseUri"/>. Returns false for empty values,
    /// pure fragments and non-http(s) schemes (mailto:, javascript:, data:, tel:, ...).
    /// </summary>
    public static bool TryResolve(Uri baseUri, string? raw, bool stripTracking, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        raw = raw.Trim();
        if (raw[0] == '#') return false;
        if (!Uri.TryCreate(baseUri, raw, out var resolved)) return false;
        if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(resolved.Host)) return false;

        url = Normalize(resolved, stripTracking);
        return true;
    }

    /// <summary>Parses user input such as "example.com" or "https://example.com/a" into a seed URL.</summary>
    public static bool TryParseSeed(string? input, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;

        url = Normalize(parsed, stripTracking: false);
        return true;
    }

    /// <summary>Drops the fragment and (optionally) well-known tracking parameters.</summary>
    public static Uri Normalize(Uri uri, bool stripTracking = true)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (stripTracking && !string.IsNullOrEmpty(uri.Query))
            builder.Query = StripTracking(uri.Query);
        return builder.Uri;
    }

    private static string StripTracking(string query)
    {
        var kept = new List<string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (IsTracking(name)) continue;
            kept.Add(part);
        }

        return string.Join('&', kept);
    }

    private static bool IsTracking(string name) =>
        name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParameters.Contains(name);
}
