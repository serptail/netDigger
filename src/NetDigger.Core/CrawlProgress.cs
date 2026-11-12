namespace NetDigger.Core;

/// <summary>A point-in-time view of a running crawl, suitable for driving a progress display.</summary>
public sealed record CrawlProgress(
    int Discovered,
    int Fetched,
    int Probed,
    int Failed,
    int Broken,
    int Blocked,
    int Skipped,
    int Queued,
    TimeSpan Elapsed,
    string? CurrentUrl);

public sealed record CrawlSummary(
    CrawlStatus Status,
    DateTimeOffset StartedAt,
    CrawlProgress Final);

/// <summary>Thread-safe counters shared by all workers.</summary>
internal sealed class CrawlStats
{
    private int _discovered, _fetched, _probed, _failed, _broken, _blocked, _skipped;

    public void Discovered() => Interlocked.Increment(ref _discovered);
    public void Fetched() => Interlocked.Increment(ref _fetched);
    public void Probed() => Interlocked.Increment(ref _probed);
    public void Failed() => Interlocked.Increment(ref _failed);
    public void Broken() => Interlocked.Increment(ref _broken);
    public void Blocked() => Interlocked.Increment(ref _blocked);
    public void Skipped() => Interlocked.Increment(ref _skipped);

    public CrawlProgress Snapshot(int queued, TimeSpan elapsed, string? currentUrl) => new(
        Volatile.Read(ref _discovered),
        Volatile.Read(ref _fetched),
        Volatile.Read(ref _probed),
        Volatile.Read(ref _failed),
        Volatile.Read(ref _broken),
        Volatile.Read(ref _blocked),
        Volatile.Read(ref _skipped),
        queued,
        elapsed,
        currentUrl);
}
