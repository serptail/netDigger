using System.Globalization;
using System.Text.RegularExpressions;

namespace NetDigger.Core.Http;

/// <summary>
/// A parsed robots.txt following RFC 9309: group selection by product token, longest-match precedence
/// with Allow winning ties, and * / $ wildcards.
/// </summary>
public sealed class RobotsTxt
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private sealed record Rule(bool Allow, int Length, Regex Pattern);

    private sealed class Group
    {
        public List<string> Agents { get; } = new();
        public List<(bool Allow, string Pattern)> Rules { get; } = new();
        public double? CrawlDelaySeconds { get; set; }
    }

    private readonly Rule[] _rules;

    private RobotsTxt(Rule[] rules, TimeSpan? crawlDelay, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
        Sitemaps = sitemaps;
    }

    public static RobotsTxt AllowAll { get; } = new(Array.Empty<Rule>(), null, Array.Empty<string>());

    public TimeSpan? CrawlDelay { get; }

    /// <summary>Sitemap URLs declared anywhere in the file.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    public static RobotsTxt Parse(string content, string agentToken)
    {
        var groups = new List<Group>();
        var sitemaps = new List<string>();
        Group? current = null;
        var lastLineWasAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (field)
            {
                case "user-agent":
                    if (!lastLineWasAgent || current is null)
                    {
                        current = new Group();
                        groups.Add(current);
                    }

                    current.Agents.Add(value.ToLowerInvariant());
                    lastLineWasAgent = true;
                    break;

                case "allow":
                case "disallow":
                    lastLineWasAgent = false;
                    current?.Rules.Add((field == "allow", value));
                    break;

                case "crawl-delay":
                    lastLineWasAgent = false;
                    if (current is not null
                        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds >= 0)
                    {
                        current.CrawlDelaySeconds = seconds;
                    }

                    break;

                case "sitemap":
                    if (value.Length > 0) sitemaps.Add(value);
                    break;
            }
        }

        var token = agentToken.ToLowerInvariant();
        var matching = groups.Where(g => g.Agents.Contains(token)).ToList();
        if (matching.Count == 0) matching = groups.Where(g => g.Agents.Contains("*")).ToList();

        var rules = matching
            .SelectMany(g => g.Rules)
            .Select(r => Compile(r.Allow, r.Pattern))
            .OfType<Rule>()
            .ToArray();

        var delaySeconds = matching.Select(g => g.CrawlDelaySeconds).FirstOrDefault(d => d.HasValue);
        TimeSpan? delay = delaySeconds.HasValue ? TimeSpan.FromSeconds(delaySeconds.Value) : null;

        return new RobotsTxt(rules, delay, sitemaps);
    }

    /// <param name="pathAndQuery">The request target, e.g. <c>/a/b?x=1</c>.</param>
    public bool IsAllowed(string pathAndQuery)
    {
        if (_rules.Length == 0) return true;

        var path = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        if (path.StartsWith("/robots.txt", StringComparison.Ordinal)) return true;

        Rule? best = null;
        foreach (var rule in _rules)
        {
            bool matches;
            try
            {
                matches = rule.Pattern.IsMatch(path);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!matches) continue;

            if (best is null
                || rule.Length > best.Length
                || (rule.Length == best.Length && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static Rule? Compile(bool allow, string pattern)
    {
        // An empty Disallow means "allow everything"; an empty Allow is meaningless.
        if (pattern.Length == 0) return null;

        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        var regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (anchored ? "$" : string.Empty);

        return new Rule(
            allow,
            pattern.Length,
            new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout));
    }
}
