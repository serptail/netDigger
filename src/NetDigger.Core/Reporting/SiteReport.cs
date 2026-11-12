namespace NetDigger.Core.Reporting;

public sealed record CountItem(string Label, int Count);

public sealed record BrokenLink(
    string Url, int? Status, string? Error, string Kind, bool InScope, int InboundLinks, string? FirstReferrer);

public sealed record RedirectItem(string Url, string Target, int? Status);

public sealed record PageIssue(string Category, string Url, string? Detail);

public sealed record MetricItem(string Url, long Value, string? Extra);

public sealed record FindingSummary(string Category, string Value, int Pages, string? Detail);

public sealed record HostInfo(
    string Host,
    string? Server,
    string? PoweredBy,
    string? TlsSubject,
    string? TlsIssuer,
    string? TlsNotAfter,
    string? TlsError,
    IReadOnlyList<string> MissingSecurityHeaders);

/// <summary>Everything the analysis stage derives from a crawl database.</summary>
public sealed class SiteReport
{
    public IReadOnlyDictionary<string, string> Info { get; init; } = new Dictionary<string, string>();
    public int TotalUrls { get; init; }
    public int InScopeUrls { get; init; }
    public int PagesAnalysed { get; init; }
    public IReadOnlyList<CountItem> ByKind { get; init; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> ByStatus { get; init; } = Array.Empty<CountItem>();
    public IReadOnlyList<CountItem> ByHost { get; init; } = Array.Empty<CountItem>();
    public IReadOnlyList<BrokenLink> BrokenLinks { get; init; } = Array.Empty<BrokenLink>();
    public IReadOnlyList<RedirectItem> Redirects { get; init; } = Array.Empty<RedirectItem>();
    public IReadOnlyList<PageIssue> PageIssues { get; init; } = Array.Empty<PageIssue>();
    public IReadOnlyList<string> OrphanPages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MetricItem> SlowestPages { get; init; } = Array.Empty<MetricItem>();
    public IReadOnlyList<MetricItem> LargestResources { get; init; } = Array.Empty<MetricItem>();
    public IReadOnlyList<PageIssue> SensitiveFiles { get; init; } = Array.Empty<PageIssue>();
    public IReadOnlyList<FindingSummary> Findings { get; init; } = Array.Empty<FindingSummary>();
    public IReadOnlyList<HostInfo> Hosts { get; init; } = Array.Empty<HostInfo>();
}
