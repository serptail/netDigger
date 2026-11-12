using System.Collections.Concurrent;
using NetDigger.Core;

namespace NetDigger.App;

/// <summary>Persists everything to the real sink and mirrors resource rows to the UI feed.</summary>
internal sealed class LiveSink(ICrawlSink inner, ConcurrentQueue<ResourceRecord> feed) : ICrawlSink
{
    public ValueTask WriteAsync(CrawlRecord record, CancellationToken cancellationToken = default)
    {
        if (record is ResourceRecord resource) feed.Enqueue(resource);
        return inner.WriteAsync(record, cancellationToken);
    }

    public ValueTask CompleteAsync() => inner.CompleteAsync();
}
