using System.Collections.Concurrent;

namespace NetDigger.Core.Http;

/// <summary>
/// Spaces out request starts per host so a crawl never hammers one server, no matter how many workers run.
/// Requests may still overlap once started; only their start times are spread out.
/// </summary>
internal sealed class HostThrottle
{
    private sealed class HostState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long NextSlotTicks;
    }

    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task WaitAsync(string hostKey, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;

        var state = _hosts.GetOrAdd(hostKey, _ => new HostState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = state.NextSlotTicks - Environment.TickCount64;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);

            state.NextSlotTicks = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
