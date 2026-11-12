using System.Text.RegularExpressions;

namespace NetDigger.Core;

/// <summary>How far the crawler is allowed to wander from the seed URLs.</summary>
public enum CrawlScope
{
    /// <summary>Only the exact host(s) of the seeds (www. is treated as equivalent to the bare domain).</summary>
    SameHost,

    /// <summary>The seed host(s) and any of their subdomains.</summary>
    SameSite,

    /// <summary>No restriction. Use with a small page limit.</summary>
    Unrestricted
}

/// <summary>Immutable configuration for a single crawl.</summary>
public sealed class CrawlOptions
{
    public required IReadOnlyList<Uri> Seeds { get; init; }

    /// <summary>Maximum link depth from a seed (seeds are depth 0).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Maximum number of URLs that will be requested with GET and analysed.</summary>
    public int MaxPages { get; init; } = 500;

    /// <summary>Number of parallel workers.</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>Minimum spacing between request starts to the same host.</summary>
    public TimeSpan PerHostDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Retries for transient failures (timeouts, 429, 502-504, connection errors).</summary>
    public int MaxRetries { get; init; } = 2;

    public CrawlScope Scope { get; init; } = CrawlScope.SameHost;

    public bool RespectRobotsTxt { get; init; } = true;

    /// <summary>Discover URLs from sitemap.xml and sitemaps declared in robots.txt.</summary>
    public bool UseSitemaps { get; init; } = true;

    /// <summary>HEAD-check in-scope non-page resources (images, PDFs, ...) for status, type and size.</summary>
    public bool ProbeResources { get; init; } = true;

    /// <summary>HEAD-check links that point outside the crawl scope (broken-link detection).</summary>
    public bool CheckExternalLinks { get; init; } = true;

    /// <summary>Scan in-scope JavaScript files for absolute URLs (API endpoints, CDNs, ...).</summary>
    public bool AnalyzeScripts { get; init; } = true;

    public bool StripTrackingParameters { get; init; } = true;

    /// <summary>Largest response body that will be downloaded for analysis.</summary>
    public long MaxBodyBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Only crawl URLs matching at least one of these patterns (when non-empty).</summary>
    public IReadOnlyList<Regex> Include { get; init; } = Array.Empty<Regex>();

    /// <summary>Never crawl URLs matching any of these patterns.</summary>
    public IReadOnlyList<Regex> Exclude { get; init; } = Array.Empty<Regex>();

    /// <summary>
    /// When set, only these non-page resource kinds are collected. Pages are always crawled
    /// because they are how everything else is found.
    /// </summary>
    public IReadOnlySet<ResourceKind>? CollectKinds { get; init; }

    public string UserAgent { get; init; } = DefaultUserAgent;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Accept invalid TLS certificates. The problem is still recorded for the host.</summary>
    public bool AcceptInvalidCertificates { get; init; }

    public const string DefaultUserAgent = "netDigger/2.0 (+https://github.com/serptail/netDigger)";

    /// <summary>The product token used to match robots.txt groups.</summary>
    public string RobotsAgentToken
    {
        get
        {
            var product = UserAgent.Split(' ', '/')[0];
            return string.IsNullOrWhiteSpace(product) ? "netdigger" : product.ToLowerInvariant();
        }
    }

    public bool Collects(ResourceKind kind) =>
        kind == ResourceKind.Page || CollectKinds is null || CollectKinds.Contains(kind);

    public void Validate()
    {
        if (Seeds is null || Seeds.Count == 0)
            throw new ArgumentException("At least one seed URL is required.", nameof(Seeds));
        foreach (var seed in Seeds)
        {
            if (!seed.IsAbsoluteUri || (seed.Scheme != Uri.UriSchemeHttp && seed.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException($"Seed '{seed}' must be an absolute http(s) URL.", nameof(Seeds));
        }

        if (MaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (Concurrency is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(Concurrency), "Must be between 1 and 128.");
        if (PerHostDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PerHostDelay));
        if (RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (MaxBodyBytes < 1024) throw new ArgumentOutOfRangeException(nameof(MaxBodyBytes));
        if (string.IsNullOrWhiteSpace(UserAgent)) throw new ArgumentException("User agent must not be empty.", nameof(UserAgent));
    }
}
