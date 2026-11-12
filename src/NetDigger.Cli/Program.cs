using Microsoft.Extensions.Logging;
using NetDigger.Cli;
using NetDigger.Core;
using NetDigger.Core.Reporting;
using NetDigger.Core.Storage;

const string Help = """
    netDigger - polite site crawler and resource discovery tool

    Usage:
      netdigger crawl <url>... [options]
      netdigger report <crawl.db> [--html FILE] [--json FILE] [--csv FILE] [--urls FILE] [--by-kind DIR]

    Crawl options:
      --depth N           Maximum link depth (default 3)
      --max-pages N       Maximum pages to fetch and analyse (default 500)
      --scope MODE        host (default), site (incl. subdomains) or any
      --concurrency N     Parallel workers (default 8)
      --delay MS          Minimum delay between requests to one host (default 150)
      --timeout SECONDS   Per-request timeout (default 20)
      --include REGEX     Only crawl URLs matching REGEX (repeatable)
      --exclude REGEX     Never crawl URLs matching REGEX (repeatable)
      --kinds LIST        Only collect these non-page kinds, e.g. pdf,image,archive
      --header "N: V"     Extra request header (repeatable)
      --user-agent TEXT   Override the User-Agent
      --out DIR           Output directory (default ./netdigger-out/<host>-<time>)
      --no-robots         Ignore robots.txt (only for sites you own or have permission to test)
      --no-sitemap        Do not read sitemaps
      --no-probe          Do not HEAD-check images, documents and other assets
      --no-external       Do not check external links
      --no-scripts        Do not mine JavaScript files for URLs
      --insecure          Accept invalid TLS certificates
      -v, -vv             More logging

    Output (in the output directory): crawl.db, report.html, resources.csv, urls/<kind>.txt
    """;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine(Help);
        return 0;
    }

    return args[0] switch
    {
        "crawl" => await CrawlAsync(args.Skip(1).ToList()),
        "report" => Report(args.Skip(1).ToList()),
        _ => throw new UsageException($"Unknown command '{args[0]}'. Try --help.")
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static async Task<int> CrawlAsync(List<string> args)
{
    var parsed = CrawlArguments.Parse(args);
    var options = parsed.ToOptions();

    var outDir = Path.GetFullPath(parsed.OutputDirectory
        ?? Path.Combine("netdigger-out", $"{parsed.Seeds[0].Host}-{DateTime.Now:yyyyMMdd-HHmmss}"));
    var dbPath = Path.Combine(outDir, "crawl.db");

    using var loggerFactory = LoggerFactory.Create(builder => builder
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
        .SetMinimumLevel(parsed.Verbosity switch { 0 => LogLevel.Warning, 1 => LogLevel.Information, _ => LogLevel.Debug }));

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        Console.Error.WriteLine("\nStopping... saving what has been found so far (Ctrl+C again to force quit).");
        Console.CancelKeyPress += (_, _) => Environment.Exit(130);
    };

    Console.WriteLine($"Crawling {string.Join(", ", parsed.Seeds)} -> {outDir}");

    CrawlSummary summary;
    await using (var store = SqliteCrawlStore.Create(dbPath))
    using (var engine = new CrawlEngine(options, store, loggerFactory.CreateLogger("netDigger")))
    {
        var progress = Console.IsErrorRedirected || parsed.Verbosity > 0
            ? null
            : new Progress<CrawlProgress>(p => Console.Error.Write(
                $"\r  discovered {p.Discovered}  fetched {p.Fetched}  probed {p.Probed}  broken {p.Broken}  queued {p.Queued}   "));

        summary = await engine.RunAsync(progress, cts.Token);
    }

    Console.Error.WriteLine();
    CrawlExporter.ExportReportHtml(dbPath, Path.Combine(outDir, "report.html"));
    CrawlExporter.ExportResourcesCsv(dbPath, Path.Combine(outDir, "resources.csv"));
    CrawlExporter.ExportUrlsByKind(dbPath, Path.Combine(outDir, "urls"));

    var f = summary.Final;
    Console.WriteLine($"{summary.Status} in {f.Elapsed:mm\\:ss}: {f.Discovered} URLs, {f.Fetched} pages/files fetched, " +
                      $"{f.Probed} probed, {f.Broken} broken, {f.Failed} failed, {f.Blocked} blocked by robots.txt, {f.Skipped} beyond limits.");
    if (summary.Status == CrawlStatus.PageLimitReached)
        Console.WriteLine("Page limit reached; raise --max-pages to crawl further.");
    Console.WriteLine($"Report: {Path.Combine(outDir, "report.html")}");

    return summary.Status == CrawlStatus.Cancelled ? 130 : 0;
}

static int Report(List<string> args)
{
    if (args.Count == 0) throw new UsageException("Give the path to a crawl.db.");
    var db = args[0];
    var wrote = false;

    for (var i = 1; i < args.Count; i++)
    {
        var option = args[i];
        if (i + 1 >= args.Count) throw new UsageException($"Option {option} needs a value.");
        var target = args[++i];

        switch (option)
        {
            case "--html": CrawlExporter.ExportReportHtml(db, target); break;
            case "--json": CrawlExporter.ExportReportJson(db, target); break;
            case "--csv": CrawlExporter.ExportResourcesCsv(db, target); break;
            case "--urls": CrawlExporter.ExportUrlList(db, target); break;
            case "--by-kind": CrawlExporter.ExportUrlsByKind(db, target); break;
            default: throw new UsageException($"Unknown option '{option}'.");
        }

        Console.WriteLine($"Wrote {target}");
        wrote = true;
    }

    if (!wrote) throw new UsageException("Nothing to do: pass --html, --json, --csv, --urls or --by-kind.");
    return 0;
}
