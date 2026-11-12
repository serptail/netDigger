using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using HtmlAgilityPack;

namespace netDigger
{
    public class UrlDetailsFetcher
    {
        private static readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; rv:68.0) Gecko/20100101 Firefox/68.0" }
            }
        };

        public async Task<string> FetchUrlDetails(string url, Action<string, string> updateLog)
        {
            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var htmlContent = await response.Content.ReadAsStringAsync();

                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                string title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();
                string metaDescription = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.Attributes["content"]?.Value;
                string metaKeywords = doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.Attributes["content"]?.Value;
                string canonicalUrl = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.Attributes["href"]?.Value;

                var ogTags = doc.DocumentNode.SelectNodes("//meta[starts-with(@property, 'og:')]")
                    ?.ToDictionary(node => node.Attributes["property"]?.Value, node => node.Attributes["content"]?.Value);

                var twitterTags = doc.DocumentNode.SelectNodes("//meta[starts-with(@name, 'twitter:')]")
                    ?.ToDictionary(node => node.Attributes["name"]?.Value, node => node.Attributes["content"]?.Value);

                var links = doc.DocumentNode.SelectNodes("//a[@href]")
                    ?.Select(a => a.GetAttributeValue("href", null))
                    .Where(href => !string.IsNullOrEmpty(href))
                    .ToList();

                var imageCount = doc.DocumentNode.SelectNodes("//img")?.Count ?? 0;
                var videoCount = doc.DocumentNode.SelectNodes("//video")?.Count ?? 0;
                var audioCount = doc.DocumentNode.SelectNodes("//audio")?.Count ?? 0;
                var scriptCount = doc.DocumentNode.SelectNodes("//script")?.Count ?? 0;
                var internalScripts = doc.DocumentNode.SelectNodes("//script[not(@src)]")?.Count ?? 0;
                var externalScripts = doc.DocumentNode.SelectNodes("//script[@src]")?.Count ?? 0;
                var inlineStyles = doc.DocumentNode.SelectNodes("//style")?.Count ?? 0;

                var csp = response.Headers.Contains("Content-Security-Policy")
                    ? response.Headers.GetValues("Content-Security-Policy").FirstOrDefault()
                    : null;

                var headers = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
                var responseSize = response.Content.Headers.ContentLength ?? 0;
                var responseTime = response.Headers.Date?.Subtract(response.RequestMessage.Headers.Date ?? DateTimeOffset.Now).TotalMilliseconds ?? 0;
                var statusCode = response.StatusCode;
                var serverType = response.Headers.Server?.ToString();
                var charset = response.Content.Headers.ContentType?.CharSet;
                var htmlVersion = doc.DocumentNode.SelectSingleNode("/html")?.Attributes["doctype"]?.Value ?? "Unknown";
                var language = doc.DocumentNode.SelectSingleNode("//html")?.Attributes["lang"]?.Value;

                var forms = doc.DocumentNode.SelectNodes("//form")
                    ?.Select(form =>
                    {
                        var action = form.GetAttributeValue("action", "");
                        var method = form.GetAttributeValue("method", "get");
                        var inputs = form.SelectNodes(".//input")
                            ?.Select(input =>
                            {
                                var type = input.GetAttributeValue("type", "text");
                                var name = input.GetAttributeValue("name", "");
                                var value = input.GetAttributeValue("value", "");
                                return new { type, name, value };
                            })
                            .ToList();

                        return new { action, method, inputs };
                    })
                    .ToList();

                var internalLinks = links?.Count(l => l.StartsWith(url, StringComparison.OrdinalIgnoreCase)) ?? 0;
                var externalLinks = links?.Count(l => !l.StartsWith(url, StringComparison.OrdinalIgnoreCase)) ?? 0;
                var followLinks = doc.DocumentNode.SelectNodes("//a[@rel!='nofollow']")?.Count ?? 0;
                var noFollowLinks = doc.DocumentNode.SelectNodes("//a[@rel='nofollow']")?.Count ?? 0;

                // Fetch SSL certificate details
                var sslCertificateDetails = await FetchSslCertificateDetails(url);

                // Advanced SEO metrics and details
                var seoDetails = await FetchSeoDetails(doc);

                string details = $"URL: {url}\n\n";
                details += $"Title: {title ?? "N/A"}\n";
                details += $"Meta Description: {metaDescription ?? "N/A"}\n";
                details += $"Meta Keywords: {metaKeywords ?? "N/A"}\n";
                details += $"Canonical URL: {canonicalUrl ?? "N/A"}\n";
                details += $"Open Graph Tags: {(ogTags != null ? string.Join(", ", ogTags.Select(kvp => $"{kvp.Key}: {kvp.Value}")) : "N/A")}\n";
                details += $"Twitter Card Tags: {(twitterTags != null ? string.Join(", ", twitterTags.Select(kvp => $"{kvp.Key}: {kvp.Value}")) : "N/A")}\n";
                details += $"Total Links: {links?.Count ?? 0}\n";
                details += $"Internal Links: {internalLinks}\n";
                details += $"External Links: {externalLinks}\n";
                details += $"Follow Links: {followLinks}\n";
                details += $"No-Follow Links: {noFollowLinks}\n";
                details += $"Total Images: {imageCount}\n";
                details += $"Total Videos: {videoCount}\n";
                details += $"Total Audio: {audioCount}\n";
                details += $"Total Scripts: {scriptCount}\n";
                details += $"Internal Scripts: {internalScripts}\n";
                details += $"External Scripts: {externalScripts}\n";
                details += $"Inline Styles: {inlineStyles}\n";
                details += $"Content Security Policy: {csp ?? "N/A"}\n";
                details += $"HTTP Headers: {headers}\n";
                details += $"Response Size: {responseSize} bytes\n";
                details += $"Response Time: {responseTime} ms\n";
                details += $"HTTP Status Code: {(int)statusCode} {statusCode}\n";
                details += $"Server Type: {serverType ?? "N/A"}\n";
                details += $"Charset: {charset ?? "N/A"}\n";
                details += $"HTML Version: {htmlVersion}\n";
                details += $"Language: {language ?? "N/A"}\n";
                details += $"SSL Certificate: {sslCertificateDetails}\n";
                details += $"SEO Details: {seoDetails}\n";
                details += $"Forms: {(forms != null ? string.Join("\n", forms.Select(f => $"Action: {f.action}, Method: {f.method}, Inputs: {string.Join(", ", f.inputs.Select(i => $"{i.type} {i.name} {i.value}"))}")) : "N/A")}\n";

                return details;
            }
            catch (Exception ex)
            {
                updateLog($"Error fetching URL details: {ex.Message}", "ERROR");
                return null;
            }
        }

        private async Task<string> FetchSslCertificateDetails(string url)
        {
            try
            {
                var uri = new Uri(url);
                using (var tcpClient = new TcpClient(uri.Host, 443))
                {
                    using (var sslStream = new SslStream(tcpClient.GetStream(), false, (sender, certificate, chain, sslPolicyErrors) => true))
                    {
                        await sslStream.AuthenticateAsClientAsync(uri.Host);
                        var cert = new X509Certificate2(sslStream.RemoteCertificate);
                        return $"Subject: {cert.Subject}, Issuer: {cert.Issuer}, Valid From: {cert.NotBefore}, Valid Until: {cert.NotAfter}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Unable to fetch SSL certificate details: {ex.Message}";
            }
        }

        private Task<string> FetchSeoDetails(HtmlDocument doc)
        {
            // Example of additional SEO metrics you might want to fetch
            var h1Count = doc.DocumentNode.SelectNodes("//h1")?.Count ?? 0;
            var h2Count = doc.DocumentNode.SelectNodes("//h2")?.Count ?? 0;
            var h3Count = doc.DocumentNode.SelectNodes("//h3")?.Count ?? 0;
            var altAttributes = doc.DocumentNode.SelectNodes("//img[@alt]")?.Count ?? 0;

            return Task.FromResult($"H1 Tags: {h1Count}, H2 Tags: {h2Count}, H3 Tags: {h3Count}, Images with Alt attributes: {altAttributes}");
        }
    }
}
