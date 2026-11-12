using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Channels;
using AngleSharp.Html.Parser;
using PuppeteerSharp;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;

namespace netDigger
{
    public class Scraper
    {
        private static readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; rv:68.0) Gecko/20100101 Firefox/68.0" }
            }
        };

        private static readonly List<string> ScrapeContentTypes = new List<string> { "text/plain", "text/html", "application/json", "application/xml" };
        private static readonly int MaxRetries = 5;
        private static readonly int RateLimitDelay = 1000;
        private static readonly double BackoffFactor = 2.0;

        private readonly LogHandler logHandler;
        private Channel<string> downloadChannel;
        private ConcurrentDictionary<string, bool> processedUrls;

        public Scraper(LogHandler logHandler)
        {
            this.logHandler = logHandler;
            Reset();
        }

        public void Reset()
        {
            downloadChannel = Channel.CreateUnbounded<string>();
            processedUrls = new ConcurrentDictionary<string, bool>();
        }

        public async Task<bool> ScrapeAsync(string url, List<string> scrapedUrls, CancellationToken cancellationToken)
        {
            Reset();

            await logHandler.UpdateLogAsync($"Starting scrape for URL: {url}", "INFO");
            await downloadChannel.Writer.WriteAsync(url, cancellationToken);
            int processedUrlsCount = 0;

            var maxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1);
            var tasks = Enumerable.Range(0, maxDegreeOfParallelism).Select(_ => Task.Run(async () =>
            {
                try
                {
                    while (await downloadChannel.Reader.WaitToReadAsync(cancellationToken))
                    {
                        if (downloadChannel.Reader.TryRead(out var currentUrl))
                        {
                            int currentThreadId = Thread.CurrentThread.ManagedThreadId;
                            await logHandler.UpdateLogAsync($"Processing URL: {currentUrl}", "INFO", currentThreadId);
                            bool result = await ProcessUrl(currentUrl, scrapedUrls, currentThreadId, cancellationToken);
                            if (!result)
                            {
                                await logHandler.UpdateLogAsync($"Failed to process URL: {currentUrl}", "ERROR", currentThreadId);
                                break;
                            }

                            Interlocked.Increment(ref processedUrlsCount);
                            await Task.Delay(RateLimitDelay, cancellationToken); // Rate limiting
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    await logHandler.UpdateLogAsync($"Task was canceled", "INFO");
                }
                catch (Exception ex)
                {
                    await logHandler.UpdateLogAsync($"Unexpected error: {ex.Message}", "ERROR");
                }
            }, cancellationToken)).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (TaskCanceledException)
            {
                await logHandler.UpdateLogAsync($"Scrape task was canceled", "INFO");
            }

            await logHandler.UpdateLogAsync($"Scrape completed for URL: {url}. Processed {processedUrlsCount} URLs.", "INFO");
            return !cancellationToken.IsCancellationRequested;
        }

        private async Task<bool> ProcessUrl(string url, List<string> scrapedUrls, int threadId, CancellationToken cancellationToken)
        {
            int retries = MaxRetries;
            bool skipGet = true;
            HttpResponseMessage headResponse = null;
            double backoffDelay = 1.0;

            while (retries > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await logHandler.UpdateLogAsync($"Sending HEAD request to URL: {url}", "INFO", threadId);
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                    headResponse = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (headResponse.Content.Headers.ContentType != null &&
                        ScrapeContentTypes.Any(ct => headResponse.Content.Headers.ContentType.ToString().Contains(ct)))
                    {
                        skipGet = false;
                        await logHandler.UpdateLogAsync($"HEAD request successful for URL: {url}. Status: {headResponse.StatusCode}", "INFO", threadId);
                        break;
                    }
                    else
                    {
                        string contentType = headResponse.Content.Headers.ContentType?.ToString() ?? "unknown";
                        await logHandler.UpdateLogAsync($"Skipping URL: {url} due to unsupported content type: {contentType}", "WARNING", threadId);
                        return true;
                    }
                }
                catch (HttpRequestException ex)
                {
                    await logHandler.UpdateLogAsync($"HTTP request error for URL: {url}: {ex.Message}. Retrying... ({MaxRetries - retries + 1}/{MaxRetries})", "ERROR", threadId);
                    await Task.Delay(TimeSpan.FromSeconds(backoffDelay), cancellationToken);
                    backoffDelay *= BackoffFactor;
                    retries--;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    await logHandler.UpdateLogAsync($"Request timeout for URL: {url}. Retrying... ({MaxRetries - retries + 1}/{MaxRetries})", "ERROR", threadId);
                    await Task.Delay(TimeSpan.FromSeconds(backoffDelay), cancellationToken);
                    backoffDelay *= BackoffFactor;
                    retries--;
                }
                catch (TaskCanceledException)
                {
                    await logHandler.UpdateLogAsync($"Task was canceled for URL: {url}", "ERROR", threadId);
                    return false;
                }
                catch (Exception ex)
                {
                    await logHandler.UpdateLogAsync($"Unexpected error for URL: {url}: {ex.Message}. Retrying... ({MaxRetries - retries + 1}/{MaxRetries})", "ERROR", threadId);
                    await Task.Delay(TimeSpan.FromSeconds(backoffDelay), cancellationToken);
                    backoffDelay *= BackoffFactor;
                    retries--;
                }
            }

            if (skipGet || retries == 0)
            {
                await logHandler.UpdateLogAsync($"Max retries exceeded for URL: {url}", "WARNING", threadId);
                return false;
            }

            try
            {
                await logHandler.UpdateLogAsync($"Sending GET request to URL: {url}", "INFO", threadId);
                var response = await client.GetStringAsync(url, cancellationToken);

                var contentType = headResponse.Content.Headers.ContentType?.ToString();

                if (contentType.Contains("text/html"))
                {
                    var parser = new HtmlParser();
                    var document = await parser.ParseDocumentAsync(response, cancellationToken);
                    await ProcessHtmlContent(url, document, scrapedUrls, threadId, cancellationToken);
                }
                else if (contentType.Contains("application/json"))
                {
                    var json = JToken.Parse(response);
                    await ProcessJsonContent(url, json, scrapedUrls, threadId, cancellationToken);
                }
                else if (contentType.Contains("application/xml"))
                {
                    var xml = XDocument.Parse(response);
                    await ProcessXmlContent(url, xml, scrapedUrls, threadId, cancellationToken);
                }
                else
                {
                    await logHandler.UpdateLogAsync($"Unsupported content type for URL: {url}", "WARNING", threadId);
                }
            }
            catch (Exception ex)
            {
                await logHandler.UpdateLogAsync($"Error processing URL: {url}: {ex.Message}", "ERROR", threadId);
                return false;
            }

            return true;
        }

        private async Task ProcessHtmlContent(string url, AngleSharp.Dom.IDocument document, List<string> scrapedUrls, int threadId, CancellationToken cancellationToken)
        {
            var anchorNodes = document.QuerySelectorAll("a[href]");
            var hiddenElements = document.QuerySelectorAll("[style*='display:none'],[hidden]");
            var metaTags = document.QuerySelectorAll("meta");

            foreach (var node in anchorNodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                string href = node.GetAttribute("href");
                if (Uri.TryCreate(new Uri(url), href, out var absoluteUri) &&
                    processedUrls.TryAdd(absoluteUri.ToString(), true))
                {
                    await downloadChannel.Writer.WriteAsync(absoluteUri.ToString(), cancellationToken);
                    lock (scrapedUrls)
                    {
                        scrapedUrls.Add(absoluteUri.ToString());
                    }

                    await logHandler.UpdateLogAsync($"Found URL: {absoluteUri}", "INFO", threadId);
                }
            }

            foreach (var element in hiddenElements)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var elementInfo = GetElementInfo(element);
                await logHandler.UpdateLogAsync($"Found hidden element: {elementInfo}", "INFO", threadId);
            }

            foreach (var metaTag in metaTags)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await logHandler.UpdateLogAsync($"Found meta tag: {metaTag.OuterHtml}", "INFO", threadId);
            }
        }

        private string GetElementInfo(AngleSharp.Dom.IElement element)
        {
            var tag = element.TagName.ToLower();
            var id = element.Id;
            var classList = element.ClassList.ToArray();
            var classNames = string.Join(" ", classList);
            var info = $"Tag: {tag}, ID: {id}, Class: {classNames}";
            return info;
        }

        private async Task ProcessJsonContent(string url, JToken json, List<string> scrapedUrls, int threadId, CancellationToken cancellationToken)
        {
            IEnumerable<JToken> tokens = json is JContainer container ? container.DescendantsAndSelf() : new[] { json };

            foreach (var token in tokens)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (token is JValue value && Uri.TryCreate(value.ToString(), UriKind.Absolute, out var absoluteUri) &&
                    processedUrls.TryAdd(absoluteUri.ToString(), true))
                {
                    await downloadChannel.Writer.WriteAsync(absoluteUri.ToString(), cancellationToken);
                    lock (scrapedUrls)
                    {
                        scrapedUrls.Add(absoluteUri.ToString());
                    }

                    await logHandler.UpdateLogAsync($"Found URL in JSON: {absoluteUri}", "INFO", threadId);
                }
            }
        }

        private async Task ProcessXmlContent(string url, XDocument xml, List<string> scrapedUrls, int threadId, CancellationToken cancellationToken)
        {
            foreach (var element in xml.Descendants())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var href = element.Attribute("href")?.Value ?? element.Value;
                if (Uri.TryCreate(new Uri(url), href, out var absoluteUri) &&
                    processedUrls.TryAdd(absoluteUri.ToString(), true))
                {
                    await downloadChannel.Writer.WriteAsync(absoluteUri.ToString(), cancellationToken);
                    lock (scrapedUrls)
                    {
                        scrapedUrls.Add(absoluteUri.ToString());
                    }

                    await logHandler.UpdateLogAsync($"Found URL in XML: {absoluteUri}", "INFO", threadId);
                }
            }
        }
    }
}
