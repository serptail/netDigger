using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace NetDigger.Core.Extraction;

/// <summary>A URL referenced by a page, with enough context to classify and store it.</summary>
public sealed record ExtractedLink(Uri Url, LinkType Type, ResourceKind? KindHint, string? Anchor, bool NoFollow);

public sealed record ExtractedFinding(FindingCategory Category, string Value, string? Detail);

/// <summary>Everything netDigger learns from a single HTML document.</summary>
public sealed class HtmlAnalysis
{
    public string? Title { get; internal set; }
    public string? Description { get; internal set; }
    public string? Canonical { get; internal set; }
    public string? Language { get; internal set; }
    public string? MetaRobots { get; internal set; }
    public string? Generator { get; internal set; }
    public string? FirstHeading { get; internal set; }
    public int H1Count { get; internal set; }
    public int H2Count { get; internal set; }
    public int WordCount { get; internal set; }

    /// <summary>Hash of the normalised visible text; empty for pages with almost no text.</summary>
    public string ContentHash { get; internal set; } = string.Empty;

    public Dictionary<string, string> OpenGraph { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TwitterCard { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExtractedLink> Links { get; } = new();
    public List<ExtractedFinding> Findings { get; } = new();
}

/// <summary>
/// Parses HTML once and extracts links (including media, scripts, styles and forms), SEO metadata,
/// forms, hidden elements, comments and contact details.
/// </summary>
public static partial class HtmlAnalyzer
{
    private const int MaxLinks = 5000;
    private const int MaxFindingsPerCategory = 25;
    private const int MaxScriptChars = 500_000;
    private const int MaxTextChars = 1_000_000;
    private const int MinWordsForHash = 10;

    [GeneratedRegex(@"url\s*=\s*['""]?(?<u>[^'"";\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefreshRegex();

    public static HtmlAnalysis Analyze(
        string html,
        Uri pageUrl,
        bool stripTrackingParameters = true,
        bool scanInlineScripts = true)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var collector = new Collector(pageUrl, stripTrackingParameters, scanInlineScripts);
        collector.Run(document);
        return collector.Result;
    }

    private sealed class Collector
    {
        private static readonly HashSet<string> NonVisibleTags = new(StringComparer.Ordinal)
        {
            "html", "head", "meta", "link", "title", "base", "script", "style", "noscript", "template"
        };

        private readonly Uri _pageUrl;
        private readonly bool _stripTracking;
        private readonly bool _scanScripts;
        private readonly HashSet<(string Url, LinkType Type)> _seenLinks = new();
        private readonly HashSet<(FindingCategory, string)> _seenFindings = new();
        private readonly Dictionary<FindingCategory, int> _findingCounts = new();
        private readonly StringBuilder _text = new();
        private Uri _baseUrl;
        private int _scriptChars;

        public Collector(Uri pageUrl, bool stripTracking, bool scanScripts)
        {
            _pageUrl = pageUrl;
            _baseUrl = pageUrl;
            _stripTracking = stripTracking;
            _scanScripts = scanScripts;
        }

        public HtmlAnalysis Result { get; } = new();

        public void Run(IDocument document)
        {
            _baseUrl = ResolveBase(document);
            Result.Title = Clean(document.Title, 300);
            Result.Language = Clean(document.DocumentElement?.GetAttribute("lang"), 35);

            var stack = new Stack<INode>();
            if (document.DocumentElement is not null) stack.Push(document.DocumentElement);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                switch (node)
                {
                    case IElement element:
                        VisitElement(element);
                        if (element.LocalName is not ("script" or "style" or "template"))
                        {
                            var children = element.ChildNodes;
                            for (var i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
                        }

                        break;
                    case IText text:
                        // The <title> lives in <head> and is not body content.
                        if (text.ParentElement?.LocalName != "title" && _text.Length < MaxTextChars)
                            _text.Append(text.Data).Append(' ');
                        break;
                    case IComment comment:
                        AddComment(comment.Data);
                        break;
                }
            }

            FinishText();
        }

        private Uri ResolveBase(IDocument document)
        {
            var href = document.QuerySelector("base[href]")?.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(href)
                && Uri.TryCreate(_pageUrl, href.Trim(), out var baseUri)
                && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
            {
                return baseUri;
            }

            return _pageUrl;
        }

        private void VisitElement(IElement element)
        {
            switch (element.LocalName)
            {
                case "a":
                case "area":
                    AddLink(
                        element.GetAttribute("href"),
                        LinkType.Anchor,
                        anchor: element.LocalName == "a" ? Clean(element.TextContent, 120) : null,
                        noFollow: HasNoFollow(element));
                    break;

                case "link":
                    VisitLinkTag(element);
                    break;

                case "img":
                    AddLink(element.GetAttribute("src"), LinkType.Image, ResourceKind.Image);
                    AddSrcSet(element.GetAttribute("srcset"), LinkType.Image, ResourceKind.Image);
                    break;

                case "source":
                    var container = element.ParentElement?.LocalName;
                    if (container is "video" or "audio")
                    {
                        AddLink(element.GetAttribute("src"), LinkType.Media,
                            container == "video" ? ResourceKind.Video : ResourceKind.Audio);
                    }
                    else
                    {
                        AddLink(element.GetAttribute("src"), LinkType.Image, ResourceKind.Image);
                    }

                    AddSrcSet(element.GetAttribute("srcset"), LinkType.Image, ResourceKind.Image);
                    break;

                case "video":
                    AddLink(element.GetAttribute("src"), LinkType.Media, ResourceKind.Video);
                    AddLink(element.GetAttribute("poster"), LinkType.Image, ResourceKind.Image);
                    break;

                case "audio":
                    AddLink(element.GetAttribute("src"), LinkType.Media, ResourceKind.Audio);
                    break;

                case "track":
                    AddLink(element.GetAttribute("src"), LinkType.Media, ResourceKind.Text);
                    break;

                case "iframe":
                case "frame":
                case "embed":
                    AddLink(element.GetAttribute("src"), LinkType.Frame);
                    break;

                case "object":
                    AddLink(element.GetAttribute("data"), LinkType.Frame);
                    break;

                case "script":
                    VisitScript(element);
                    break;

                case "style":
                    foreach (var css in TextUrlExtractor.FindCssUrls(element.TextContent))
                        AddLink(css, LinkType.Reference);
                    break;

                case "form":
                    VisitForm(element);
                    break;

                case "meta":
                    VisitMeta(element);
                    break;

                case "h1":
                    Result.H1Count++;
                    Result.FirstHeading ??= Clean(element.TextContent, 200);
                    break;

                case "h2":
                    Result.H2Count++;
                    break;
            }

            // Lazy-loading attributes used by many image/video libraries.
            var lazySource = element.GetAttribute("data-src");
            if (!string.IsNullOrWhiteSpace(lazySource))
            {
                var isImage = element.LocalName is "img" or "source";
                AddLink(lazySource, isImage ? LinkType.Image : LinkType.Reference, isImage ? ResourceKind.Image : null);
            }

            AddSrcSet(element.GetAttribute("data-srcset"), LinkType.Image, ResourceKind.Image);

            CheckHidden(element);
        }

        private void VisitLinkTag(IElement element)
        {
            var rel = (element.GetAttribute("rel") ?? string.Empty).ToLowerInvariant();
            var href = element.GetAttribute("href");

            if (rel.Contains("stylesheet", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Stylesheet, ResourceKind.Stylesheet);
            }
            else if (rel.Contains("icon", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Icon, ResourceKind.Image);
            }
            else if (rel.Contains("canonical", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Canonical);
                if (Result.Canonical is null && UrlNormalizer.TryResolve(_baseUrl, href, _stripTracking, out var canonical))
                    Result.Canonical = canonical.AbsoluteUri;
            }
            else if (rel.Contains("alternate", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Alternate);
            }
            else if (rel.Contains("preload", StringComparison.Ordinal)
                     || rel.Contains("modulepreload", StringComparison.Ordinal)
                     || rel.Contains("prefetch", StringComparison.Ordinal))
            {
                var (type, kind) = PreloadTarget(element.GetAttribute("as"));
                AddLink(href, type, kind);
            }
            else if (rel.Contains("next", StringComparison.Ordinal) || rel.Contains("prev", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Anchor);
            }
            else if (rel.Contains("manifest", StringComparison.Ordinal))
            {
                AddLink(href, LinkType.Reference, ResourceKind.Json);
            }
        }

        private static (LinkType Type, ResourceKind? Kind) PreloadTarget(string? asValue)
        {
            switch ((asValue ?? string.Empty).ToLowerInvariant())
            {
                case "style": return (LinkType.Stylesheet, ResourceKind.Stylesheet);
                case "script": return (LinkType.Script, ResourceKind.Script);
                case "image": return (LinkType.Image, ResourceKind.Image);
                case "font": return (LinkType.Reference, ResourceKind.Font);
                default: return (LinkType.Reference, null);
            }
        }

        private void VisitScript(IElement element)
        {
            var src = element.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(src))
            {
                AddLink(src, LinkType.Script, ResourceKind.Script);
                return;
            }

            if (!_scanScripts || _scriptChars >= MaxScriptChars) return;

            var text = element.TextContent;
            if (string.IsNullOrWhiteSpace(text)) return;
            _scriptChars += text.Length;

            var isJson = (element.GetAttribute("type") ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> urls = isJson ? JsonLinkExtractor.FindUrls(text) : TextUrlExtractor.FindAbsoluteUrls(text);
            foreach (var url in urls) AddLink(url, LinkType.Reference);
        }

        private void VisitForm(IElement form)
        {
            var action = form.GetAttribute("action");
            var method = (form.GetAttribute("method") ?? "get").Trim().ToUpperInvariant();

            string target;
            if (string.IsNullOrWhiteSpace(action))
            {
                target = _pageUrl.AbsoluteUri;
            }
            else if (UrlNormalizer.TryResolve(_baseUrl, action, _stripTracking, out var actionUrl))
            {
                target = actionUrl.AbsoluteUri;
                AddLink(action, LinkType.Form);
            }
            else
            {
                target = action.Trim();
            }

            var fields = new List<string>();
            var hasPassword = false;
            var hasUpload = false;

            foreach (var field in form.QuerySelectorAll("input,select,textarea,button"))
            {
                var name = field.GetAttribute("name");
                var type = field.LocalName == "input"
                    ? (field.GetAttribute("type") ?? "text").ToLowerInvariant()
                    : field.LocalName;

                if (type == "password") hasPassword = true;
                if (type == "file") hasUpload = true;
                if (string.IsNullOrEmpty(name) && type is "submit" or "button" or "reset" or "image") continue;
                if (fields.Count < 20) fields.Add(string.IsNullOrEmpty(name) ? type : $"{type}:{name}");
            }

            var flags = (hasPassword ? " [password]" : string.Empty) + (hasUpload ? " [file upload]" : string.Empty);
            AddFinding(FindingCategory.Form, $"{method} {target}", $"fields: {string.Join(", ", fields)}{flags}");
        }

        private void VisitMeta(IElement element)
        {
            var name = element.GetAttribute("name")?.Trim().ToLowerInvariant();
            var property = element.GetAttribute("property")?.Trim().ToLowerInvariant();
            var httpEquiv = element.GetAttribute("http-equiv")?.Trim().ToLowerInvariant();
            var content = element.GetAttribute("content");

            if (httpEquiv == "refresh" && content is not null)
            {
                var match = RefreshRegex().Match(content);
                if (match.Success) AddLink(match.Groups["u"].Value, LinkType.Redirect);
            }

            if (string.IsNullOrWhiteSpace(content)) return;
            content = content.Trim();

            if (property is not null && property.StartsWith("og:", StringComparison.Ordinal))
                Result.OpenGraph[property] = content;

            if (name is null) return;

            if (name.StartsWith("twitter:", StringComparison.Ordinal))
            {
                Result.TwitterCard[name] = content;
                return;
            }

            switch (name)
            {
                case "description":
                    Result.Description ??= Clean(content, 500);
                    break;
                case "robots":
                case "googlebot":
                    Result.MetaRobots = Result.MetaRobots is null ? content : $"{Result.MetaRobots}, {content}";
                    break;
                case "generator":
                    Result.Generator ??= Clean(content, 120);
                    AddFinding(FindingCategory.Technology, $"generator: {Clean(content, 120)}", null);
                    break;
            }
        }

        private void CheckHidden(IElement element)
        {
            if (NonVisibleTags.Contains(element.LocalName)) return;
            if (element.LocalName == "input" && string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase))
                return;

            var style = element.GetAttribute("style");
            var hiddenByStyle = false;
            if (!string.IsNullOrEmpty(style))
            {
                var compact = new string(style.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
                hiddenByStyle = compact.Contains("display:none", StringComparison.Ordinal)
                                || compact.Contains("visibility:hidden", StringComparison.Ordinal);
            }

            if (!element.HasAttribute("hidden") && !hiddenByStyle) return;

            var descriptor = element.LocalName
                             + (string.IsNullOrEmpty(element.Id) ? string.Empty : "#" + element.Id)
                             + (element.ClassList.Length == 0 ? string.Empty : "." + string.Join('.', element.ClassList));
            AddFinding(FindingCategory.HiddenElement, descriptor, Clean(element.TextContent, 80));
        }

        private void AddComment(string? data)
        {
            var text = Clean(data, 300);
            if (text is null || text.Length < 4) return;
            if (text.StartsWith("[if", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<![endif", StringComparison.OrdinalIgnoreCase))
                return;
            AddFinding(FindingCategory.Comment, text, null);
        }

        private void AddFinding(FindingCategory category, string value, string? detail)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _findingCounts.TryGetValue(category, out var count);
            if (count >= MaxFindingsPerCategory) return;
            if (!_seenFindings.Add((category, value))) return;

            _findingCounts[category] = count + 1;
            Result.Findings.Add(new ExtractedFinding(category, value, detail));
        }

        private void AddContact(FindingCategory category, string raw)
        {
            var queryStart = raw.IndexOf('?');
            if (queryStart >= 0) raw = raw[..queryStart];

            foreach (var part in Uri.UnescapeDataString(raw).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = part.Trim();
                if (category == FindingCategory.Email && !value.Contains('@')) continue;
                if (value.Length is 0 or > 120) continue;
                AddFinding(category, value, null);
            }
        }

        private void AddLink(
            string? raw,
            LinkType type,
            ResourceKind? hint = null,
            string? anchor = null,
            bool noFollow = false)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                AddContact(FindingCategory.Email, trimmed[7..]);
                return;
            }

            if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                AddContact(FindingCategory.Phone, trimmed[4..]);
                return;
            }

            if (Result.Links.Count >= MaxLinks) return;
            if (!UrlNormalizer.TryResolve(_baseUrl, trimmed, _stripTracking, out var url)) return;
            if (!_seenLinks.Add((url.AbsoluteUri, type))) return;

            Result.Links.Add(new ExtractedLink(url, type, hint, anchor, noFollow));
        }

        private void AddSrcSet(string? srcset, LinkType type, ResourceKind? hint)
        {
            if (string.IsNullOrWhiteSpace(srcset)) return;

            foreach (var candidate in srcset.Split(','))
            {
                var url = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (url is null || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                AddLink(url, type, hint);
            }
        }

        private void FinishText()
        {
            var raw = _text.ToString();
            var normalised = new StringBuilder(raw.Length);
            var words = 0;
            var inWord = false;

            foreach (var c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                    continue;
                }

                if (!inWord)
                {
                    words++;
                    if (normalised.Length > 0) normalised.Append(' ');
                    inWord = true;
                }

                normalised.Append(char.ToLowerInvariant(c));
            }

            Result.WordCount = words;
            if (words >= MinWordsForHash)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised.ToString()));
                Result.ContentHash = Convert.ToHexString(hash)[..32].ToLowerInvariant();
            }
        }

        private static bool HasNoFollow(IElement element)
        {
            var rel = element.GetAttribute("rel");
            if (string.IsNullOrEmpty(rel)) return false;

            return rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token =>
                token.Equals("nofollow", StringComparison.OrdinalIgnoreCase)
                || token.Equals("ugc", StringComparison.OrdinalIgnoreCase)
                || token.Equals("sponsored", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Collapses whitespace, trims, truncates; returns null for empty input.</summary>
        private static string? Clean(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength];
        }
    }
}
