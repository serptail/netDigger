using NetDigger.Core;
using NetDigger.Core.Extraction;
using Xunit;

namespace NetDigger.Core.Tests;

public class HtmlAnalyzerTests
{
    private static readonly Uri Page = new("https://example.com/dir/page.html");

    private static HtmlAnalysis Analyze(string html) => HtmlAnalyzer.Analyze(html, Page);

    [Fact]
    public void Extracts_metadata()
    {
        var a = Analyze("""
            <html lang="en"><head><title> My   Page </title>
            <meta name="description" content="Hello">
            <meta name="robots" content="noindex">
            <meta property="og:title" content="OG"><meta property="og:title" content="OG again">
            <link rel="canonical" href="/canonical">
            </head><body><h1>Heading</h1><h2>a</h2><h2>b</h2></body></html>
            """);

        Assert.Equal("My Page", a.Title);
        Assert.Equal("Hello", a.Description);
        Assert.Equal("noindex", a.MetaRobots);
        Assert.Equal("en", a.Language);
        Assert.Equal("https://example.com/canonical", a.Canonical);
        Assert.Equal("Heading", a.FirstHeading);
        Assert.Equal(1, a.H1Count);
        Assert.Equal(2, a.H2Count);
        Assert.Equal("OG again", a.OpenGraph["og:title"]); // duplicate keys must not throw
    }

    [Fact]
    public void Extracts_and_classifies_links()
    {
        var a = Analyze("""
            <html><head><link rel="stylesheet" href="s.css"><script src="/app.js"></script></head>
            <body><a href="other.html" rel="nofollow">Other</a>
            <a href="#frag">skip</a><a href="mailto:me@example.com?subject=x">mail</a>
            <img src="/i.png" srcset="/i2.png 2x, /i3.png 3x">
            <form action="/submit" method="post"><input name="q"><input type="password" name="p"></form>
            </body></html>
            """);

        Link(a, "https://example.com/dir/other.html", LinkType.Anchor, noFollow: true);
        Link(a, "https://example.com/dir/s.css", LinkType.Stylesheet);
        Link(a, "https://example.com/app.js", LinkType.Script);
        Link(a, "https://example.com/i.png", LinkType.Image);
        Link(a, "https://example.com/i3.png", LinkType.Image);
        Link(a, "https://example.com/submit", LinkType.Form);
        Assert.DoesNotContain(a.Links, l => l.Url.AbsoluteUri.Contains('#'));

        Assert.Contains(a.Findings, f => f.Category == FindingCategory.Email && f.Value == "me@example.com");
        var form = Assert.Single(a.Findings, f => f.Category == FindingCategory.Form);
        Assert.Contains("POST", form.Value);
        Assert.Contains("[password]", form.Detail);
    }

    [Fact]
    public void Honors_base_href()
    {
        var a = Analyze("""<html><head><base href="https://cdn.example.net/root/"></head><body><a href="x">x</a></body></html>""");
        Link(a, "https://cdn.example.net/root/x", LinkType.Anchor);
    }

    [Fact]
    public void Finds_hidden_elements_comments_and_script_urls()
    {
        var a = Analyze("""
            <html><body><div id="d" class="x y" style="display: none">secret</div>
            <!-- TODO remove https://internal.example.com/debug -->
            <script>fetch("https://api.example.com/v1\/items")</script></body></html>
            """);

        Assert.Contains(a.Findings, f => f.Category == FindingCategory.HiddenElement && f.Value == "div#d.x.y");
        Assert.Contains(a.Findings, f => f.Category == FindingCategory.Comment && f.Value.Contains("TODO"));
        Assert.Contains(a.Links, l => l.Url.Host == "api.example.com" && l.Type == LinkType.Reference);
    }

    [Fact]
    public void Identical_text_gives_identical_hash_and_tiny_pages_give_none()
    {
        const string body = "<p>one two three four five six seven eight nine ten eleven</p>";
        Assert.Equal(Analyze($"<html><body>{body}</body></html>").ContentHash,
            Analyze($"<html><head><title>other</title></head><body><div>{body}</div></body></html>").ContentHash);
        Assert.Equal(string.Empty, Analyze("<html><body>hi</body></html>").ContentHash);
    }

    private static void Link(HtmlAnalysis a, string url, LinkType type, bool noFollow = false) =>
        Assert.Contains(a.Links, l => l.Url.AbsoluteUri == url && l.Type == type && l.NoFollow == noFollow);
}

public class OtherExtractorTests
{
    [Fact]
    public void Parses_sitemap_and_index()
    {
        var sitemap = XmlLinkExtractor.Extract(
            """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://e.com/a</loc></url><url><loc>https://e.com/b</loc></url></urlset>""");
        Assert.Equal(XmlDocumentKind.Sitemap, sitemap.Kind);
        Assert.Equal(2, sitemap.Urls.Count);

        var index = XmlLinkExtractor.Extract(
            """<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><sitemap><loc>https://e.com/s1.xml</loc></sitemap></sitemapindex>""");
        Assert.Equal(XmlDocumentKind.SitemapIndex, index.Kind);
    }

    [Fact]
    public void Rejects_dtd_without_throwing() =>
        Assert.Equal(XmlDocumentKind.Other,
            XmlLinkExtractor.Extract("""<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x>&e;</x>""").Kind);

    [Fact]
    public void Finds_json_urls_and_ignores_invalid_json()
    {
        var urls = JsonLinkExtractor.FindUrls("""{"a":"https://e.com/x","b":[{"c":"https://e.com/y"}],"d":"not a url"}""");
        Assert.Equal(new[] { "https://e.com/x", "https://e.com/y" }, urls.OrderBy(u => u));
        Assert.Empty(JsonLinkExtractor.FindUrls("{broken"));
    }

    [Fact]
    public void Finds_css_urls_but_not_data_uris()
    {
        var urls = TextUrlExtractor.FindCssUrls("""
            @import "base.css"; a{background:url('img/a.png')} b{background:url(data:image/png;base64,AAA)} c{src:url("f.woff2")}
            """).ToList();
        Assert.Contains("img/a.png", urls);
        Assert.Contains("f.woff2", urls);
        Assert.Contains("base.css", urls);
        Assert.DoesNotContain(urls, u => u.StartsWith("data:"));
    }
}
