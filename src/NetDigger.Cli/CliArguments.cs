using System.Text.RegularExpressions;
using NetDigger.Core;
using NetDigger.Core.Extraction;

namespace NetDigger.Cli;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Parsed command line for <c>netdigger crawl</c>.</summary>
internal sealed class CrawlArguments
{
    public List<Uri> Seeds { get; } = new();
    public int MaxDepth { get; private set; } = 3;
    public int MaxPages { get; private set; } = 500;
    public int Concurrency { get; private set; } = 8;
    public int DelayMs { get; private set; } = 150;
    public int TimeoutSeconds { get; private set; } = 20;
    public CrawlScope Scope { get; private set; } = CrawlScope.SameHost;
    public bool RespectRobots { get; private set; } = true;
    public bool UseSitemaps { get; private set; } = true;
    public bool ProbeResources { get; private set; } = true;
    public bool CheckExternal { get; private set; } = true;
    public bool AnalyzeScripts { get; private set; } = true;
    public bool Insecure { get; private set; }
    public string? OutputDirectory { get; private set; }
    public string UserAgent { get; private set; } = CrawlOptions.DefaultUserAgent;
    public int Verbosity { get; private set; }
    public List<Regex> Include { get; } = new();
    public List<Regex> Exclude { get; } = new();
    public HashSet<ResourceKind>? Kinds { get; private set; }
    public Dictionary<string, string> Headers { get; } = new();

    public static CrawlArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CrawlArguments();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            string Next()
            {
                if (i + 1 >= args.Count) throw new UsageException($"Option {arg} needs a value.");
                return args[++i];
            }

            int NextInt(int min, int max)
            {
                var text = Next();
                if (!int.TryParse(text, out var value) || value < min || value > max)
                    throw new UsageException($"Option {arg} expects a number between {min} and {max}, got '{text}'.");
                return value;
            }

            switch (arg)
            {
                case "--depth": result.MaxDepth = NextInt(0, 50); break;
                case "--max-pages": result.MaxPages = NextInt(1, 10_000_000); break;
                case "--concurrency": result.Concurrency = NextInt(1, 128); break;
                case "--delay": result.DelayMs = NextInt(0, 60_000); break;
                case "--timeout": result.TimeoutSeconds = NextInt(1, 600); break;
                case "--scope": result.Scope = ParseScope(Next()); break;
                case "--no-robots": result.RespectRobots = false; break;
                case "--no-sitemap": result.UseSitemaps = false; break;
                case "--no-probe": result.ProbeResources = false; break;
                case "--no-external": result.CheckExternal = false; break;
                case "--no-scripts": result.AnalyzeScripts = false; break;
                case "--insecure": result.Insecure = true; break;
                case "--out": result.OutputDirectory = Next(); break;
                case "--user-agent": result.UserAgent = Next(); break;
                case "--include": result.Include.Add(ParseRegex(Next())); break;
                case "--exclude": result.Exclude.Add(ParseRegex(Next())); break;
                case "--kinds": result.Kinds = ParseKinds(Next()); break;
                case "--header": AddHeader(result.Headers, Next()); break;
                case "-v": result.Verbosity = Math.Max(result.Verbosity, 1); break;
                case "-vv": result.Verbosity = 2; break;
                default:
                    if (arg.StartsWith('-')) throw new UsageException($"Unknown option '{arg}'.");
                    if (!UrlNormalizer.TryParseSeed(arg, out var seed))
                        throw new UsageException($"'{arg}' is not a valid URL.");
                    result.Seeds.Add(seed);
                    break;
            }
        }

        if (result.Seeds.Count == 0) throw new UsageException("Give at least one URL to crawl.");
        return result;
    }

    public CrawlOptions ToOptions() => new()
    {
        Seeds = Seeds,
        MaxDepth = MaxDepth,
        MaxPages = MaxPages,
        Concurrency = Concurrency,
        PerHostDelay = TimeSpan.FromMilliseconds(DelayMs),
        RequestTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
        Scope = Scope,
        RespectRobotsTxt = RespectRobots,
        UseSitemaps = UseSitemaps,
        ProbeResources = ProbeResources,
        CheckExternalLinks = CheckExternal,
        AnalyzeScripts = AnalyzeScripts,
        AcceptInvalidCertificates = Insecure,
        UserAgent = UserAgent,
        Include = Include,
        Exclude = Exclude,
        CollectKinds = Kinds,
        Headers = Headers
    };

    private static CrawlScope ParseScope(string value) => value.ToLowerInvariant() switch
    {
        "host" => CrawlScope.SameHost,
        "site" => CrawlScope.SameSite,
        "any" => CrawlScope.Unrestricted,
        _ => throw new UsageException("--scope must be host, site or any.")
    };

    private static Regex ParseRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            throw new UsageException($"Invalid regular expression '{pattern}': {ex.Message}");
        }
    }

    private static HashSet<ResourceKind> ParseKinds(string list)
    {
        var kinds = new HashSet<ResourceKind>();
        foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ResourceKind>(name, ignoreCase: true, out var kind))
                throw new UsageException($"Unknown kind '{name}'. Valid kinds: {string.Join(", ", Enum.GetNames<ResourceKind>())}.");
            kinds.Add(kind);
        }

        return kinds;
    }

    private static void AddHeader(Dictionary<string, string> headers, string raw)
    {
        var colon = raw.IndexOf(':');
        if (colon <= 0) throw new UsageException("--header expects 'Name: value'.");
        headers[raw[..colon].Trim()] = raw[(colon + 1)..].Trim();
    }
}
