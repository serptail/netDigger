namespace NetDigger.Core;

/// <summary>
/// Receives every record the engine produces. Implementations must be safe to call from many threads.
/// </summary>
public interface ICrawlSink
{
    ValueTask WriteAsync(CrawlRecord record, CancellationToken cancellationToken = default);

    /// <summary>Called exactly once when the crawl has finished producing records.</summary>
    ValueTask CompleteAsync();
}
