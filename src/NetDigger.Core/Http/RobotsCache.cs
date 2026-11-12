using System.Collections.Concurrent;

namespace NetDigger.Core.Http;

/// <summary>
/// Fetches and caches robots.txt once per origin. A missing, unreachable or unparseable file means
/// "everything allowed", which matches how the vast majority of crawlers behave.
/// </summary>
internal sealed class RobotsCache
{
    private const int MaxRedirects = 5;

    private readonly HttpFetcher _fetcher;
    private readonly string _agentToken;
    private readonly Func<Uri, RobotsTxt, ValueTask>? _onLoaded;
    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsTxt>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RobotsCache(HttpFetcher fetcher, string agentToken, Func<Uri, RobotsTxt, ValueTask>? onLoaded = null)
    {
        _fetcher = fetcher;
        _agentToken = agentToken;
        _onLoaded = onLoaded;
    }

    public Task<RobotsTxt> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        var origin = new Uri(url.GetLeftPart(UriPartial.Authority));
        var entry = _cache.GetOrAdd(
            origin.AbsoluteUri,
            _ => new Lazy<Task<RobotsTxt>>(() => LoadAsync(origin, cancellationToken)));
        return entry.Value;
    }

    private async Task<RobotsTxt> LoadAsync(Uri origin, CancellationToken cancellationToken)
    {
        RobotsTxt robots;
        try
        {
            robots = await DownloadAsync(origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            robots = RobotsTxt.AllowAll;
        }

        if (_onLoaded is not null)
            await _onLoaded(origin, robots).ConfigureAwait(false);

        return robots;
    }

    private async Task<RobotsTxt> DownloadAsync(Uri origin, CancellationToken cancellationToken)
    {
        var url = new Uri(origin, "/robots.txt");

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var result = await _fetcher
                .FetchAsync(url, new FetchRequest(FetchMethod.Get, ReadBody: true), cancellationToken)
                .ConfigureAwait(false);

            if (result.IsRedirect)
            {
                url = result.RedirectLocation!;
                continue;
            }

            return result.IsSuccess && result.Body is not null
                ? RobotsTxt.Parse(result.Body, _agentToken)
                : RobotsTxt.AllowAll;
        }

        return RobotsTxt.AllowAll;
    }
}
