namespace NetDigger.Core;

/// <summary>Broad classification of a discovered URL.</summary>
public enum ResourceKind
{
    Page,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Text,
    Csv,
    Image,
    Video,
    Audio,
    Json,
    Xml,
    Stylesheet,
    Script,
    Font,
    Database,
    Archive,
    Other
}

/// <summary>How one resource references another.</summary>
public enum LinkType
{
    Anchor,
    Image,
    Stylesheet,
    Script,
    Media,
    Frame,
    Form,
    Icon,
    Canonical,
    Alternate,
    Redirect,
    Sitemap,
    Reference
}

/// <summary>How a URL first entered the crawl.</summary>
public enum DiscoveryMethod
{
    Seed,
    Link,
    Sitemap,
    Redirect,
    Reference
}

/// <summary>What the crawler ultimately did with a URL.</summary>
public enum FetchOutcome
{
    /// <summary>Requested with GET and (if parseable) analysed.</summary>
    Fetched,

    /// <summary>Checked with HEAD only (status, type, size).</summary>
    Probed,

    /// <summary>Catalogued but intentionally not requested (external with checking off, form targets, ...).</summary>
    NotRequested,

    BlockedByRobots,

    /// <summary>In scope, but beyond the depth or page budget.</summary>
    SkippedByLimit,

    /// <summary>The request failed at the network level (DNS, TLS, timeout, ...).</summary>
    Failed
}

public enum FindingCategory
{
    Email,
    Phone,
    Form,
    HiddenElement,
    Comment,
    Technology
}

public enum CrawlStatus
{
    Completed,
    Cancelled,
    PageLimitReached
}

/// <summary>Base type for everything the engine hands to an <see cref="ICrawlSink"/>.</summary>
public abstract record CrawlRecord;

/// <summary>One row per unique URL discovered during the crawl.</summary>
public sealed record ResourceRecord(
    string Url,
    string Host,
    ResourceKind Kind,
    FetchOutcome Outcome,
    bool InScope,
    int Depth,
    DiscoveryMethod DiscoveredVia,
    string? Referrer,
    int? StatusCode,
    string? ContentType,
    long? SizeBytes,
    int? ResponseMs,
    string? RedirectTo,
    string? Error,
    DateTimeOffset RecordedAt) : CrawlRecord;

/// <summary>Extracted metadata for an HTML page.</summary>
public sealed record PageRecord(
    string Url,
    string? Title,
    string? Description,
    string? Canonical,
    string? Language,
    string? MetaRobots,
    string? Generator,
    string? FirstHeading,
    int H1Count,
    int H2Count,
    int WordCount,
    string ContentHash,
    IReadOnlyDictionary<string, string> OpenGraph,
    IReadOnlyDictionary<string, string> TwitterCard) : CrawlRecord;

/// <summary>A directed reference from one URL to another.</summary>
public sealed record LinkRecord(
    string Source,
    string Target,
    LinkType Type,
    string? Anchor,
    bool NoFollow) : CrawlRecord;

/// <summary>Something interesting found on a page (forms, contact details, hidden elements, ...).</summary>
public sealed record FindingRecord(
    string Url,
    FindingCategory Category,
    string Value,
    string? Detail) : CrawlRecord;

/// <summary>Per-host information: server fingerprint, TLS certificate, security headers.</summary>
public sealed record HostRecord(
    string Host,
    string? Server,
    string? PoweredBy,
    string? TlsSubject,
    string? TlsIssuer,
    DateTimeOffset? TlsNotAfter,
    string? TlsError,
    IReadOnlyList<string> MissingSecurityHeaders) : CrawlRecord;

/// <summary>Key/value metadata about the crawl itself (seeds, timings, options).</summary>
public sealed record InfoRecord(string Key, string Value) : CrawlRecord;
