using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetDigger.Core.Extraction;

namespace NetDigger.Core.Http;

internal enum FetchMethod
{
    Head,
    Get
}

/// <param name="Method">HEAD or GET.</param>
/// <param name="ReadBody">Download and decode the body when its type is parseable (GET only).</param>
/// <param name="GzipFile">The URL points at a gzip file (e.g. sitemap.xml.gz) that should be decompressed.</param>
internal readonly record struct FetchRequest(FetchMethod Method, bool ReadBody = false, bool GzipFile = false);

internal sealed record CertificateInfo(string Subject, string Issuer, DateTimeOffset NotAfter, string? Error);

/// <summary>The outcome of a single HTTP exchange. <see cref="StatusCode"/> is 0 when no response was received.</summary>
internal sealed class FetchResult
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public required Uri RequestUrl { get; init; }
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public Uri? RedirectLocation { get; init; }
    public string? Body { get; init; }
    public long? BodyBytes { get; init; }
    public bool BodyTruncated { get; init; }
    public int ElapsedMs { get; init; }
    public string? Error { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    public bool IsSuccess => StatusCode is >= 200 and < 300;
    public bool IsRedirect => RedirectLocation is not null && StatusCode is 301 or 302 or 303 or 307 or 308;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A thin, defensive wrapper over <see cref="HttpClient"/>: no automatic redirects (the engine treats every hop
/// as a discovered URL), per-request timeouts, retry with backoff for transient failures, response size caps,
/// and capture of the TLS certificate seen for each host.
/// </summary>
internal sealed class HttpFetcher : IDisposable
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly CrawlOptions _options;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, CertificateInfo> _certificates = new(StringComparer.OrdinalIgnoreCase);

    static HttpFetcher()
    {
        // Lets Encoding.GetEncoding("windows-1252") and friends work for legacy pages.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HttpFetcher(CrawlOptions options)
    {
        _options = options;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;

        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        var headers = _client.DefaultRequestHeaders;
        headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        foreach (var (name, value) in options.Headers)
            headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>TLS certificates observed so far, keyed by host name.</summary>
    public IReadOnlyDictionary<string, CertificateInfo> Certificates => _certificates;

    public async Task<FetchResult> FetchAsync(Uri url, FetchRequest request, CancellationToken cancellationToken)
    {
        var result = await FetchWithRetryAsync(url, request, cancellationToken).ConfigureAwait(false);

        // Plenty of servers mishandle HEAD. Confirm with a GET (headers only) before calling a link broken.
        if (request.Method == FetchMethod.Head && result.StatusCode is 400 or 403 or 404 or 405 or 501)
        {
            result = await FetchWithRetryAsync(url, request with { Method = FetchMethod.Get, ReadBody = false }, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    public void Dispose() => _client.Dispose();

    private async Task<FetchResult> FetchWithRetryAsync(Uri url, FetchRequest request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await SendOnceAsync(url, request, stopwatch, cancellationToken).ConfigureAwait(false);
                if (IsTransientStatus(result.StatusCode) && attempt <= _options.MaxRetries)
                {
                    await Task.Delay(RetryDelay(attempt, result.RetryAfter), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // OperationCanceledException here means our own per-request timeout fired.
                var transient = ex is HttpRequestException or OperationCanceledException or IOException;
                if (transient && attempt <= _options.MaxRetries)
                {
                    await Task.Delay(RetryDelay(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new FetchResult
                {
                    RequestUrl = url,
                    StatusCode = 0,
                    ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
                    Error = Describe(ex)
                };
            }
        }
    }

    private async Task<FetchResult> SendOnceAsync(
        Uri url,
        FetchRequest request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var message = new HttpRequestMessage(
            request.Method == FetchMethod.Head ? HttpMethod.Head : HttpMethod.Get,
            url);
        using var response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var charset = response.Content.Headers.ContentType?.CharSet;
        var length = response.Content.Headers.ContentLength;

        Uri? location = null;
        if (response.Headers.Location is { } rawLocation)
            location = rawLocation.IsAbsoluteUri ? rawLocation : new Uri(url, rawLocation);

        TimeSpan? retryAfter = null;
        var retryHeader = response.Headers.RetryAfter;
        if (retryHeader?.Delta is { } delta)
            retryAfter = delta;
        else if (retryHeader?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;

        string? body = null;
        long? bodyBytes = null;
        var truncated = false;

        var wantsBody = request.Method == FetchMethod.Get
                        && request.ReadBody
                        && status is >= 200 and < 300
                        && ContentTypes.IsParseable(contentType, request.GzipFile)
                        && (length is null || length <= _options.MaxBodyBytes);
        if (wantsBody)
        {
            (body, var byteCount, truncated) = await ReadBodyAsync(response.Content, charset, request.GzipFile, timeout.Token)
                .ConfigureAwait(false);
            bodyBytes = byteCount;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);

        return new FetchResult
        {
            RequestUrl = url,
            StatusCode = status,
            ContentType = contentType,
            ContentLength = length,
            RedirectLocation = location,
            Body = body,
            BodyBytes = bodyBytes,
            BodyTruncated = truncated,
            ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
            RetryAfter = retryAfter,
            Headers = headers
        };
    }

    private async Task<(string Text, long Bytes, bool Truncated)> ReadBodyAsync(
        HttpContent content,
        string? charset,
        bool gzipFile,
        CancellationToken cancellationToken)
    {
        var limit = _options.MaxBodyBytes;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var (bytes, truncated) = await ReadCappedAsync(stream, limit, cancellationToken).ConfigureAwait(false);

        // Servers often label .xml.gz as gzip *and* send Content-Encoding: gzip, in which case the handler has
        // already decompressed it. Only decompress when the gzip magic number is actually still there.
        if (gzipFile && bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            var (inflated, inflatedTruncated) = await ReadCappedAsync(gzip, limit, cancellationToken).ConfigureAwait(false);
            bytes = inflated;
            truncated |= inflatedTruncated;
        }

        return (Decode(bytes, charset), bytes.Length, truncated);
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        Stream stream,
        long limit,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var remaining = limit - buffer.Length;
                if (read > remaining)
                {
                    buffer.Write(chunk, 0, (int)remaining);
                    return (buffer.ToArray(), true);
                }

                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', '\'', ' '));
            }
            catch (ArgumentException)
            {
                // Unknown charset label: fall back to UTF-8.
            }
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private bool ValidateCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        var host = sender switch
        {
            SslStream stream => stream.TargetHostName,
            HttpRequestMessage message => message.RequestUri?.Host,
            _ => null
        };

        if (host is not null && certificate is not null)
        {
            try
            {
                using var detail = new X509Certificate2(certificate);
                _certificates[host] = new CertificateInfo(
                    detail.Subject,
                    detail.Issuer,
                    new DateTimeOffset(detail.NotAfter),
                    errors == SslPolicyErrors.None ? null : errors.ToString());
            }
            catch (Exception)
            {
                // Certificate details are informational; never let them break a request.
            }
        }

        return errors == SslPolicyErrors.None || _options.AcceptInvalidCertificates;
    }

    private static bool IsTransientStatus(int status) => status is 408 or 429 or 502 or 503 or 504;

    private static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested)
        {
            if (requested < TimeSpan.Zero) return TimeSpan.Zero;
            return requested > MaxRetryAfter ? MaxRetryAfter : requested;
        }

        var backoffMs = 500 * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(backoffMs + Random.Shared.Next(0, 250));
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Request timed out",
        _ => ex.GetBaseException().Message
    };
}
