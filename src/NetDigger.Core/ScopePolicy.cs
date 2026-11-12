using System.Text.RegularExpressions;

namespace NetDigger.Core;

/// <summary>Decides which URLs belong to the crawl and which are merely external references.</summary>
public sealed class ScopePolicy
{
    private readonly HashSet<string> _roots;
    private readonly CrawlScope _scope;
    private readonly IReadOnlyList<Regex> _include;
    private readonly IReadOnlyList<Regex> _exclude;

    public ScopePolicy(
        IEnumerable<Uri> seeds,
        CrawlScope scope,
        IReadOnlyList<Regex>? include = null,
        IReadOnlyList<Regex>? exclude = null)
    {
        _roots = new HashSet<string>(seeds.Select(s => Canonical(s.Host)), StringComparer.OrdinalIgnoreCase);
        _scope = scope;
        _include = include ?? Array.Empty<Regex>();
        _exclude = exclude ?? Array.Empty<Regex>();
    }

    /// <summary>Whether the URL's host falls inside the crawl scope.</summary>
    public bool IsInScope(Uri url)
    {
        if (_scope == CrawlScope.Unrestricted) return true;

        var host = Canonical(url.Host);
        if (_roots.Contains(host)) return true;
        if (_scope == CrawlScope.SameHost) return false;

        foreach (var root in _roots)
        {
            if (host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Applies the user's include/exclude patterns. Excluded URLs are dropped entirely.</summary>
    public bool PassesFilters(Uri url)
    {
        var text = url.AbsoluteUri;

        if (_include.Count > 0 && !_include.Any(r => r.IsMatch(text))) return false;
        return !_exclude.Any(r => r.IsMatch(text));
    }

    /// <summary>Treats "www.example.com" and "example.com" as the same site.</summary>
    private static string Canonical(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
