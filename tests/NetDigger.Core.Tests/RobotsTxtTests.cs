using NetDigger.Core.Http;
using Xunit;

namespace NetDigger.Core.Tests;

public class RobotsTxtTests
{
    [Fact]
    public void Disallow_and_longest_allow_win()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /private/
            Allow: /private/public/
            """, "netdigger");

        Assert.False(robots.IsAllowed("/private/secret"));
        Assert.True(robots.IsAllowed("/private/public/page"));
        Assert.True(robots.IsAllowed("/other"));
    }

    [Fact]
    public void Specific_agent_group_overrides_wildcard()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /

            User-agent: NetDigger
            Disallow: /admin
            """, "netdigger");

        Assert.True(robots.IsAllowed("/index.html"));
        Assert.False(robots.IsAllowed("/admin/users"));
    }

    [Fact]
    public void Wildcards_and_end_anchor()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /*.pdf$
            Disallow: /tmp*/cache
            """, "netdigger");

        Assert.False(robots.IsAllowed("/files/a.pdf"));
        Assert.True(robots.IsAllowed("/files/a.pdf?x=1"));
        Assert.False(robots.IsAllowed("/tmp123/cache"));
    }

    [Fact]
    public void Empty_disallow_allows_everything_and_robots_itself_is_always_allowed()
    {
        var open = RobotsTxt.Parse("User-agent: *\nDisallow:", "netdigger");
        Assert.True(open.IsAllowed("/anything"));

        var closed = RobotsTxt.Parse("User-agent: *\nDisallow: /", "netdigger");
        Assert.False(closed.IsAllowed("/anything"));
        Assert.True(closed.IsAllowed("/robots.txt"));
    }

    [Fact]
    public void Parses_crawl_delay_comments_and_sitemaps()
    {
        var robots = RobotsTxt.Parse(
            "# hello\r\nUser-agent: *  # everyone\r\nCrawl-delay: 2.5\r\nSitemap: https://example.com/s.xml\r\n",
            "netdigger");

        Assert.Equal(TimeSpan.FromSeconds(2.5), robots.CrawlDelay);
        Assert.Equal(new[] { "https://example.com/s.xml" }, robots.Sitemaps);
    }
}
