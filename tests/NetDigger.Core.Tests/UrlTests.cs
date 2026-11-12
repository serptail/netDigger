using NetDigger.Core;
using NetDigger.Core.Extraction;
using Xunit;

namespace NetDigger.Core.Tests;

public class UrlNormalizerTests
{
    private static readonly Uri Base = new("https://example.com/a/b/page.html");

    [Theory]
    [InlineData("/x", "https://example.com/x")]
    [InlineData("c.html", "https://example.com/a/b/c.html")]
    [InlineData("//cdn.example.org/lib.js", "https://cdn.example.org/lib.js")]
    [InlineData("https://example.com/x#section", "https://example.com/x")]
    [InlineData("https://example.com/x?utm_source=a&id=5&fbclid=z", "https://example.com/x?id=5")]
    public void Resolves_and_normalizes(string raw, string expected)
    {
        Assert.True(UrlNormalizer.TryResolve(Base, raw, stripTracking: true, out var url));
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#top")]
    [InlineData("javascript:void(0)")]
    [InlineData("mailto:a@b.com")]
    [InlineData("tel:+123")]
    [InlineData("data:text/plain;base64,AAAA")]
    public void Rejects_non_http_and_empty(string raw) =>
        Assert.False(UrlNormalizer.TryResolve(Base, raw, true, out _));

    [Fact]
    public void Tracking_parameters_are_kept_when_disabled()
    {
        Assert.True(UrlNormalizer.TryResolve(Base, "/x?utm_source=a", stripTracking: false, out var url));
        Assert.Equal("https://example.com/x?utm_source=a", url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("http://example.com/path", "http://example.com/path")]
    public void Parses_seeds(string input, string expected)
    {
        Assert.True(UrlNormalizer.TryParseSeed(input, out var url));
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Fact]
    public void Rejects_bad_seed() => Assert.False(UrlNormalizer.TryParseSeed("ftp://example.com", out _));
}

public class ClassifierTests
{
    [Theory]
    [InlineData("https://x.com/file.PDF?download=1", ResourceKind.Pdf)]
    [InlineData("https://x.com/img/a.jpeg", ResourceKind.Image)]
    [InlineData("https://x.com/release/v2.0", ResourceKind.Page)]
    [InlineData("https://x.com/", ResourceKind.Page)]
    [InlineData("https://x.com/app.js", ResourceKind.Script)]
    [InlineData("https://x.com/site.css", ResourceKind.Stylesheet)]
    [InlineData("https://x.com/backup.sql", ResourceKind.Database)]
    public void Classifies_by_extension(string url, ResourceKind expected) =>
        Assert.Equal(expected, ResourceClassifier.FromUrl(new Uri(url)));

    [Theory]
    [InlineData("text/html", ResourceKind.Page)]
    [InlineData("application/ld+json", ResourceKind.Json)]
    [InlineData("application/rss+xml", ResourceKind.Xml)]
    [InlineData("image/webp", ResourceKind.Image)]
    [InlineData("application/pdf", ResourceKind.Pdf)]
    public void Classifies_by_content_type(string contentType, ResourceKind expected) =>
        Assert.Equal(expected, ResourceClassifier.FromContentType(contentType));

    [Fact]
    public void Unknown_content_type_is_null() =>
        Assert.Null(ResourceClassifier.FromContentType("application/octet-stream"));
}

public class ScopePolicyTests
{
    private static readonly Uri[] Seeds = { new("https://www.example.com/") };

    [Fact]
    public void SameHost_treats_www_as_equivalent_but_excludes_subdomains()
    {
        var scope = new ScopePolicy(Seeds, CrawlScope.SameHost);
        Assert.True(scope.IsInScope(new Uri("https://example.com/a")));
        Assert.True(scope.IsInScope(new Uri("https://www.example.com/a")));
        Assert.False(scope.IsInScope(new Uri("https://blog.example.com/a")));
        Assert.False(scope.IsInScope(new Uri("https://notexample.com/a")));
    }

    [Fact]
    public void SameSite_includes_subdomains_only_on_dot_boundary()
    {
        var scope = new ScopePolicy(Seeds, CrawlScope.SameSite);
        Assert.True(scope.IsInScope(new Uri("https://blog.example.com/")));
        Assert.False(scope.IsInScope(new Uri("https://badexample.com/")));
    }

    [Fact]
    public void Include_and_exclude_filters_apply()
    {
        var scope = new ScopePolicy(
            Seeds, CrawlScope.SameHost,
            include: new[] { new System.Text.RegularExpressions.Regex("/docs/") },
            exclude: new[] { new System.Text.RegularExpressions.Regex("/docs/private") });
        Assert.True(scope.PassesFilters(new Uri("https://example.com/docs/a")));
        Assert.False(scope.PassesFilters(new Uri("https://example.com/blog/a")));
        Assert.False(scope.PassesFilters(new Uri("https://example.com/docs/private/a")));
    }
}
