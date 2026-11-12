using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetDigger.Core.Extraction;
using NetDigger.Core.Http;

namespace NetDigger.Core;

/// <summary>
/// Crawls one or more sites breadth-first, staying inside the configured scope, honouring robots.txt and
/// per-host rate limits, and streams everything it learns to an <see cref="ICrawlSink"/>.
/// </summary>
/// <remarks>
/// Every unique URL produces exactly one <see cref="ResourceRecord"/>. Pages are fetched and analysed, other
/// in-scope resources and external links are probed with HEAD, and everything else is simply catalogued.
/// An engine instance is single-use.
/// </remarks>
public sealed class CrawlEngine : IDisposable
{
    private static readonly TimeSpan MaxCrawlDelay = TimeSpan.FromSeconds(30);

    private enum WorkMode
    {
        Fetch,
        Probe,
        Sitemap
    }

    private readonly record struct WorkItem(
        Uri Url,
        int Depth,
        Uri? Referrer,
        DiscoveryMethod Via,
        WorkMode Mode,
        ResourceKind Kind,
        bool InScope,
        bool Speculative);

    private readonly CrawlOptions _options;
    private readonly ICrawlSink _sink;
    private readonly ILogger _logger;
    private readonly ScopePolicy _scope;
    private readonly HttpFetcher _fetcher;
    private readonly RobotsCache _robots;
    private readonly HostThrottle _throttle = new();
    private readonly CrawlStats _stats = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _hostsRecorded = new(StringComparer.OrdinalIgnoreCase);

    private int _pending;
    private int _fetchSlots;
    private int _started;
    private string? _currentUrl;

    public CrawlEngine(CrawlOptions options, ICrawlSink sink, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        options.Validate();

        _options = options;
        _sink = sink;
        _logger = logger ?? NullLogger.Instance;
        _scope = new ScopePolicy(options.Seeds, options.Scope, options.Include, options.Exclude);
        _fetcher = new HttpFetcher(options);
        _robots = new RobotsCache(_fetcher, options.RobotsAgentToken, OnRobotsLoadedAsync);
    }

    /// <summary>Runs the crawl to completion, cancellation or the page limit. Partial results are always flushed.</summary>
    public async Task<CrawlSummary> RunAsync(
        IProgress<CrawlProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("A CrawlEngine instance can only be run once.");

        var startedAt = DateTimeOffset.UtcNow;
        _stopwatch.Start();

        using var reporterCts = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : ReportLoopAsync(progress, reporterCts.Token);

        try
        {
            await WriteStartInfoAsync(startedAt, cancellationToken).ConfigureAwait(false);
            await SeedAsync(cancellationToken).ConfigureAwait(false);

            // If nothing was queued (e.g. every seed was filtered out) there is nothing to wait for.
            if (Volatile.Read(ref _pending) == 0) _queue.Writer.TryComplete();

            var workers = Enumerable.Range(0, _options.Concurrency)
                .Select(_ => Task.Run(() => WorkerAsync(cancellationToken), CancellationToken.None))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Handled below via the token state.
        }
        finally
        {
            _queue.Writer.TryComplete();
            reporterCts.Cancel();
            await reporter.ConfigureAwait(false);
        }

        var status = cancellationToken.IsCancellationRequested
            ? CrawlStatus.Cancelled
            : Volatile.Read(ref _fetchSlots) > _options.MaxPages
                ? CrawlStatus.PageLimitReached
                : CrawlStatus.Completed;

        var final = Snapshot();
        await FinishAsync(status, final).ConfigureAwait(false);
        progress?.Report(final);

        return new CrawlSummary(status, startedAt, final);
    }

    public void Dispose() => _fetcher.Dispose();

    // ---------------------------------------------------------------------------------------------------------
    // Scheduling
    // ---------------------------------------------------------------------------------------------------------

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in _options.Seeds)
        {
            var url = UrlNormalizer.Normalize(seed, _options.StripTrackingParameters);
            if (!_scope.PassesFilters(url))
            {
                _logger.LogWarning("Seed {Url} is excluded by the include/exclude filters and will be skipped", url);
                continue;
            }

            await DiscoverAsync(url, 0, null, DiscoveryMethod.Seed, LinkType.Anchor, null, cancellationToken)
                .ConfigureAwait(false);
            origins.Add(url.GetLeftPart(UriPartial.Authority));
        }

        if (!_options.UseSitemaps) return;

        foreach (var origin in origins)
            DiscoverSitemap(new Uri(origin + "/sitemap.xml"), speculative: true);
    }

    /// <summary>
    /// Registers a URL: applies filters and scope, de-duplicates it, then either queues it or records
    /// why it will not be requested.
    /// </summary>
    private async ValueTask DiscoverAsync(
        Uri url,
        int depth,
        Uri? referrer,
        DiscoveryMethod via,
        LinkType type,
        ResourceKind? hint,
        CancellationToken cancellationToken)
    {
        if (!_scope.PassesFilters(url)) return;

        var kind = ResourceClassifier.FromUrl(url);
        if (hint is { } hinted && kind == ResourceKind.Page) kind = hinted;
        if (!_options.Collects(kind)) return;
        if (!_seen.TryAdd(url.AbsoluteUri, 0)) return;

        _stats.Discovered();
        var inScope = _scope.IsInScope(url);

        WorkMode? mode = null;
        var skipOutcome = FetchOutcome.NotRequested;

        if (type == LinkType.Form)
        {
            // Never request form targets: submitting or even GET-ing them can have side effects.
        }
        else if (inScope && ResourceClassifier.IsTraversable(kind, _options.AnalyzeScripts))
        {
            if (depth > _options.MaxDepth || Interlocked.Increment(ref _fetchSlots) > _options.MaxPages)
                skipOutcome = FetchOutcome.SkippedByLimit;
            else
                mode = WorkMode.Fetch;
        }
        else if (inScope ? _options.ProbeResources : _options.CheckExternalLinks)
        {
            mode = WorkMode.Probe;
        }

        if (mode is null)
        {
            if (skipOutcome == FetchOutcome.SkippedByLimit) _stats.Skipped();

            await _sink.WriteAsync(
                new ResourceRecord(
                    url.AbsoluteUri, url.Host, kind, skipOutcome, inScope, depth, via,
                    referrer?.AbsoluteUri, null, null, null, null, null, null, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Enqueue(new WorkItem(url, depth, referrer, via, mode.Value, kind, inScope, Speculative: false));
    }

    private void DiscoverSitemap(Uri url, bool speculative)
    {
        if (!_seen.TryAdd(url.AbsoluteUri, 0)) return;
        if (!speculative) _stats.Discovered();

        Enqueue(new WorkItem(url, 0, null, DiscoveryMethod.Sitemap, WorkMode.Sitemap, ResourceKind.Xml, true, speculative));
    }

    private void Enqueue(WorkItem item)
    {
        // The counter is raised before the write so it can never read zero while work is still outstanding.
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(item)) CompleteItem();
    }

    private void CompleteItem()
    {
        if (Interlocked.Decrement(ref _pending) == 0)
            _queue.Writer.TryComplete();
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while processing {Url}", item.Url);
                    _stats.Failed();
                    await TryWriteFailureAsync(item, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CompleteItem();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task TryWriteFailureAsync(WorkItem item, string error, CancellationToken cancellationToken)
    {
        try
        {
            await _sink.WriteAsync(MakeRecord(item, FetchOutcome.Failed, item.Kind, null, null, error), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nothing more to do.
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Processing
    // ---------------------------------------------------------------------------------------------------------

    private async Task ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _currentUrl, item.Url.AbsoluteUri);
        _logger.LogDebug("{Mode} {Url} (depth {Depth})", item.Mode, item.Url, item.Depth);

        // robots.txt is only consulted for the site being crawled; external links are just link-checked.
        RobotsTxt? robots = null;
        if (item.InScope)
            robots = await _robots.GetAsync(item.Url, cancellationToken).ConfigureAwait(false);

        if (item.Mode != WorkMode.Sitemap
            && robots is not null
            && _options.RespectRobotsTxt
            && !robots.IsAllowed(item.Url.PathAndQuery))
        {
            _stats.Blocked();
            _logger.LogInformation("Blocked by robots.txt: {Url}", item.Url);
            await _sink.WriteAsync(MakeRecord(item, FetchOutcome.BlockedByRobots, item.Kind), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var delay = _options.PerHostDelay;
        if (_options.RespectRobotsTxt && robots?.CrawlDelay is { } requested)
        {
            if (requested > MaxCrawlDelay) requested = MaxCrawlDelay;
            if (requested > delay) delay = requested;
        }

        await _throttle.WaitAsync(item.Url.Authority, delay, cancellationToken).ConfigureAwait(false);

        if (item.Mode == WorkMode.Sitemap)
            await ProcessSitemapAsync(item, cancellationToken).ConfigureAwait(false);
        else
            await ProcessResourceAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessResourceAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var fetching = item.Mode == WorkMode.Fetch;
        var request = fetching
            ? new FetchRequest(FetchMethod.Get, ReadBody: true)
            : new FetchRequest(FetchMethod.Head);

        var result = await _fetcher.FetchAsync(item.Url, request, cancellationToken).ConfigureAwait(false);

        if (result.StatusCode == 0)
        {
            _stats.Failed();
            _logger.LogWarning("{Url} failed: {Error}", item.Url, result.Error);
            await _sink.WriteAsync(MakeRecord(item, FetchOutcome.Failed, item.Kind, result), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var outcome = fetching ? FetchOutcome.Fetched : FetchOutcome.Probed;
        if (fetching) _stats.Fetched(); else _stats.Probed();
        if (result.StatusCode >= 400) _stats.Broken();

        var kind = RefineKind(item.Kind, result.ContentType);

        // Redirects are not followed blindly: the target is treated as a newly discovered URL so that scope,
        // robots.txt, limits and de-duplication all apply to it.
        if (result.IsRedirect)
        {
            UrlNormalizer.TryResolve(item.Url, result.RedirectLocation!.AbsoluteUri, _options.StripTrackingParameters, out var target);

            await _sink.WriteAsync(MakeRecord(item, outcome, kind, result, target?.AbsoluteUri), cancellationToken)
                .ConfigureAwait(false);

            if (target is not null)
            {
                await _sink.WriteAsync(
                    new LinkRecord(item.Url.AbsoluteUri, target.AbsoluteUri, LinkType.Redirect, null, false),
                    cancellationToken).ConfigureAwait(false);
                await DiscoverAsync(
                    target, item.Depth, item.Url, DiscoveryMethod.Redirect, LinkType.Redirect,
                    kind == ResourceKind.Page ? null : kind, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await _sink.WriteAsync(MakeRecord(item, outcome, kind, result), cancellationToken).ConfigureAwait(false);

        if (!fetching) return;

        await RecordHostAsync(item.Url, result, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Body is not null)
            await AnalyzeBodyAsync(item, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task AnalyzeBodyAsync(WorkItem item, FetchResult result, CancellationToken cancellationToken)
    {
        var body = result.Body!;
        var type = result.ContentType;

        if (ContentTypes.IsHtml(type))
            await AnalyzeHtmlAsync(item, body, cancellationToken).ConfigureAwait(false);
        else if (ContentTypes.IsXml(type))
            await AnalyzeXmlAsync(item, body, cancellationToken).ConfigureAwait(false);
        else if (ContentTypes.IsJson(type))
            await EmitReferencesAsync(item, JsonLinkExtractor.FindUrls(body), cancellationToken).ConfigureAwait(false);
        else if (ContentTypes.IsCss(type))
            await EmitReferencesAsync(item, TextUrlExtractor.FindCssUrls(body), cancellationToken).ConfigureAwait(false);
        else if (ContentTypes.IsJavaScript(type))
            await EmitReferencesAsync(item, TextUrlExtractor.FindAbsoluteUrls(body), cancellationToken).ConfigureAwait(false);
    }

    private async Task AnalyzeHtmlAsync(WorkItem item, string html, CancellationToken cancellationToken)
    {
        var analysis = HtmlAnalyzer.Analyze(html, item.Url, _options.StripTrackingParameters, _options.AnalyzeScripts);
        var url = item.Url.AbsoluteUri;

        await _sink.WriteAsync(
            new PageRecord(
                url, analysis.Title, analysis.Description, analysis.Canonical, analysis.Language,
                analysis.MetaRobots, analysis.Generator, analysis.FirstHeading, analysis.H1Count,
                analysis.H2Count, analysis.WordCount, analysis.ContentHash, analysis.OpenGraph, analysis.TwitterCard),
            cancellationToken).ConfigureAwait(false);

        foreach (var finding in analysis.Findings)
        {
            await _sink.WriteAsync(
                new FindingRecord(url, finding.Category, finding.Value, finding.Detail),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var link in analysis.Links)
        {
            await _sink.WriteAsync(
                new LinkRecord(url, link.Url.AbsoluteUri, link.Type, link.Anchor, link.NoFollow),
                cancellationToken).ConfigureAwait(false);

            var via = link.Type == LinkType.Reference ? DiscoveryMethod.Reference : DiscoveryMethod.Link;
            await DiscoverAsync(link.Url, item.Depth + 1, item.Url, via, link.Type, link.KindHint, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AnalyzeXmlAsync(WorkItem item, string xml, CancellationToken cancellationToken)
    {
        var extraction = XmlLinkExtractor.Extract(xml);

        if (extraction.Kind == XmlDocumentKind.Other)
            await EmitReferencesAsync(item, extraction.Urls, cancellationToken).ConfigureAwait(false);
        else
            await HandleSitemapAsync(item.Url, extraction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records and discovers URLs found in non-HTML documents (JSON, CSS, JavaScript, generic XML).</summary>
    private async Task EmitReferencesAsync(WorkItem item, IEnumerable<string> rawUrls, CancellationToken cancellationToken)
    {
        foreach (var raw in rawUrls)
        {
            if (!UrlNormalizer.TryResolve(item.Url, raw, _options.StripTrackingParameters, out var target)) continue;

            await _sink.WriteAsync(
                new LinkRecord(item.Url.AbsoluteUri, target.AbsoluteUri, LinkType.Reference, null, false),
                cancellationToken).ConfigureAwait(false);
            await DiscoverAsync(
                target, item.Depth + 1, item.Url, DiscoveryMethod.Reference, LinkType.Reference, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Sitemaps and robots.txt
    // ---------------------------------------------------------------------------------------------------------

    private ValueTask OnRobotsLoadedAsync(Uri origin, RobotsTxt robots)
    {
        if (!_options.UseSitemaps) return ValueTask.CompletedTask;

        foreach (var declared in robots.Sitemaps)
        {
            if (UrlNormalizer.TryResolve(origin, declared, stripTracking: false, out var sitemapUrl))
                DiscoverSitemap(sitemapUrl, speculative: false);
        }

        return ValueTask.CompletedTask;
    }

    private async Task ProcessSitemapAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var gzip = item.Url.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var result = await _fetcher
            .FetchAsync(item.Url, new FetchRequest(FetchMethod.Get, ReadBody: true, GzipFile: gzip), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsRedirect)
        {
            if (UrlNormalizer.TryResolve(item.Url, result.RedirectLocation!.AbsoluteUri, false, out var moved)
                && _scope.IsInScope(moved))
            {
                DiscoverSitemap(moved, item.Speculative);
            }

            return;
        }

        // The default /sitemap.xml location is only a guess, so a miss is not worth reporting.
        if (!result.IsSuccess || result.Body is null)
        {
            if (item.Speculative) return;

            if (result.StatusCode == 0) _stats.Failed(); else _stats.Broken();
            await _sink.WriteAsync(
                MakeRecord(item, result.StatusCode == 0 ? FetchOutcome.Failed : FetchOutcome.Fetched, ResourceKind.Xml, result),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var extraction = ContentTypes.IsHtml(result.ContentType)
            ? new XmlExtraction(XmlDocumentKind.Other, Array.Empty<string>()) // soft-404 page served as 200
            : XmlLinkExtractor.Extract(result.Body);

        if (extraction.Kind == XmlDocumentKind.Other)
        {
            if (!item.Speculative)
            {
                _logger.LogWarning("{Url} was declared as a sitemap but is not a valid sitemap", item.Url);
                await _sink.WriteAsync(MakeRecord(item, FetchOutcome.Fetched, ResourceKind.Xml, result), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        _stats.Fetched();
        _logger.LogInformation("Sitemap {Url}: {Count} entries", item.Url, extraction.Urls.Count);
        await _sink.WriteAsync(MakeRecord(item, FetchOutcome.Fetched, ResourceKind.Xml, result), cancellationToken)
            .ConfigureAwait(false);
        await HandleSitemapAsync(item.Url, extraction, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSitemapAsync(Uri sitemapUrl, XmlExtraction extraction, CancellationToken cancellationToken)
    {
        foreach (var raw in extraction.Urls)
        {
            if (!UrlNormalizer.TryResolve(sitemapUrl, raw, _options.StripTrackingParameters, out var target)) continue;

            await _sink.WriteAsync(
                new LinkRecord(sitemapUrl.AbsoluteUri, target.AbsoluteUri, LinkType.Sitemap, null, false),
                cancellationToken).ConfigureAwait(false);

            if (extraction.Kind == XmlDocumentKind.SitemapIndex)
            {
                if (_scope.IsInScope(target)) DiscoverSitemap(target, speculative: false);
            }
            else
            {
                await DiscoverAsync(target, 1, sitemapUrl, DiscoveryMethod.Sitemap, LinkType.Sitemap, null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Host information
    // ---------------------------------------------------------------------------------------------------------

    private async Task RecordHostAsync(Uri url, FetchResult result, CancellationToken cancellationToken)
    {
        if (!ContentTypes.IsHtml(result.ContentType)) return;
        if (!_hostsRecorded.TryAdd(url.Host, 0)) return;

        _fetcher.Certificates.TryGetValue(url.Host, out var certificate);

        var missing = new List<string>();
        if (url.Scheme == Uri.UriSchemeHttps && result.Header("Strict-Transport-Security") is null)
            missing.Add("Strict-Transport-Security");

        var csp = result.Header("Content-Security-Policy");
        if (csp is null) missing.Add("Content-Security-Policy");
        if (result.Header("X-Content-Type-Options") is null) missing.Add("X-Content-Type-Options");
        if (result.Header("X-Frame-Options") is null
            && (csp is null || !csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase)))
            missing.Add("X-Frame-Options");
        if (result.Header("Referrer-Policy") is null) missing.Add("Referrer-Policy");
        if (result.Header("Permissions-Policy") is null) missing.Add("Permissions-Policy");

        await _sink.WriteAsync(
            new HostRecord(
                url.Host,
                result.Header("Server"),
                result.Header("X-Powered-By"),
                certificate?.Subject,
                certificate?.Issuer,
                certificate?.NotAfter,
                certificate?.Error,
                missing),
            cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static ResourceKind RefineKind(ResourceKind declared, string? contentType)
    {
        var fromType = ResourceClassifier.FromContentType(contentType);
        if (fromType is { } kind) return kind;
        if (contentType is null) return declared;

        // A page candidate answering with a type we do not recognise is some other kind of file.
        return declared == ResourceKind.Page ? ResourceKind.Other : declared;
    }

    private static ResourceRecord MakeRecord(
        WorkItem item,
        FetchOutcome outcome,
        ResourceKind kind,
        FetchResult? result = null,
        string? redirectTo = null,
        string? error = null) =>
        new(
            item.Url.AbsoluteUri,
            item.Url.Host,
            kind,
            outcome,
            item.InScope,
            item.Depth,
            item.Via,
            item.Referrer?.AbsoluteUri,
            result is { StatusCode: > 0 } ? result.StatusCode : null,
            result?.ContentType,
            result?.BodyBytes ?? result?.ContentLength,
            result?.ElapsedMs,
            redirectTo,
            error ?? result?.Error,
            DateTimeOffset.UtcNow);

    private CrawlProgress Snapshot() =>
        _stats.Snapshot(Volatile.Read(ref _pending), _stopwatch.Elapsed, Volatile.Read(ref _currentUrl));

    private async Task ReportLoopAsync(IProgress<CrawlProgress> progress, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                progress.Report(Snapshot());
        }
        catch (OperationCanceledException)
        {
            // Crawl finished.
        }
    }

    private async Task WriteStartInfoAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var settings = JsonSerializer.Serialize(new
        {
            _options.MaxDepth,
            _options.MaxPages,
            _options.Concurrency,
            PerHostDelayMs = _options.PerHostDelay.TotalMilliseconds,
            Scope = _options.Scope.ToString(),
            _options.RespectRobotsTxt,
            _options.UseSitemaps,
            _options.ProbeResources,
            _options.CheckExternalLinks,
            _options.AnalyzeScripts,
            Include = _options.Include.Select(r => r.ToString()),
            Exclude = _options.Exclude.Select(r => r.ToString()),
            CollectKinds = _options.CollectKinds?.Select(k => k.ToString()),
            _options.UserAgent
        });

        await _sink.WriteAsync(new InfoRecord("seeds", string.Join('\n', _options.Seeds.Select(s => s.AbsoluteUri))), cancellationToken)
            .ConfigureAwait(false);
        await _sink.WriteAsync(new InfoRecord("started_at", startedAt.ToString("O")), cancellationToken).ConfigureAwait(false);
        await _sink.WriteAsync(new InfoRecord("options", settings), cancellationToken).ConfigureAwait(false);
        await _sink.WriteAsync(
            new InfoRecord("engine_version", typeof(CrawlEngine).Assembly.GetName().Version?.ToString() ?? "unknown"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(CrawlStatus status, CrawlProgress final)
    {
        try
        {
            await _sink.WriteAsync(new InfoRecord("finished_at", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
            await _sink.WriteAsync(new InfoRecord("status", status.ToString())).ConfigureAwait(false);
            await _sink.WriteAsync(new InfoRecord("elapsed_seconds", final.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
        }
        finally
        {
            await _sink.CompleteAsync().ConfigureAwait(false);
        }
    }
}
