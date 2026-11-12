# netDigger

A polite, scope-aware **site crawler and resource discovery tool**. Point it at a site and it maps everything
reachable: pages, documents, media, scripts, APIs referenced from JavaScript, broken links, redirects, forms,
SEO problems and basic security posture. Everything lands in one SQLite database plus an HTML report.

> v2 is a ground-up rework. The original WPF app is preserved in [`legacy/`](legacy) until its UI is rebuilt on the new core.

## What it does

- **Stays in scope.** Same host (default), same site incl. subdomains, or unrestricted. Include/exclude regexes. Depth and page limits.
- **Is polite.** Honors `robots.txt` (wildcards, `Allow`, `Crawl-delay`), per-host request spacing, retries with backoff and `Retry-After`.
- **Finds more.** Links, images/`srcset`, media, scripts, stylesheets (`url()`/`@import`), iframes, forms, meta refresh,
  JSON/XML/RSS, absolute URLs inside JavaScript and JSON-LD, sitemaps (index, `.gz`, declared in robots.txt).
- **Records every URL once** with kind, status, content type, size, response time, redirect target, depth and referrer.
- **Analyses.** Broken links (internal/external), redirects, missing/duplicate titles & descriptions, H1 problems,
  `noindex`, thin and duplicate content, orphan pages (in sitemap but never linked), slowest pages, largest files,
  possibly sensitive files (`.bak`, `.sql`, `.env`, `.git/`, archives), emails/phones, forms (flags password/upload),
  hidden elements, HTML comments, server fingerprint, TLS certificate expiry, missing security headers.
- **Exports** `report.html`, `resources.csv`/`.json`, plain URL lists, and one URL list per file kind.

## Usage

Windows app: `dotnet run --project src/NetDigger.App`. Command line:

```sh
dotnet run --project src/NetDigger.Cli -- crawl example.com --depth 4 --max-pages 2000
dotnet run --project src/NetDigger.Cli -- crawl example.com --kinds pdf,archive --scope site
dotnet run --project src/NetDigger.Cli -- report netdigger-out/example.com-.../crawl.db --html report.html
```

Run `netdigger --help` for all options. Only crawl sites you own or have permission to test.

## Layout

| Path | Purpose |
|---|---|
| `src/NetDigger.Core` | UI-free engine: `CrawlEngine`, extraction, robots, storage, reporting |
| `src/NetDigger.Cli` | Command-line front end |
| `src/NetDigger.App` | Compact Windows (WPF) app with live results, built on the same core |
| `tests/NetDigger.Core.Tests` | Unit tests for URL handling, robots.txt, scope, extractors |
| `legacy/` | The original WPF application (to be rebuilt on `NetDigger.Core`) |

## Using the engine from code

```csharp
var options = new CrawlOptions { Seeds = new[] { new Uri("https://example.com") }, MaxDepth = 3 };
await using var store = SqliteCrawlStore.Create("crawl.db");
using var engine = new CrawlEngine(options, store);
var summary = await engine.RunAsync(new Progress<CrawlProgress>(p => Console.WriteLine(p.Discovered)), ct);
var report = ReportBuilder.Build("crawl.db");
```

Any `ICrawlSink` can replace the SQLite store, which is how a GUI would stream live results.

## Releases

Run the **Release** workflow from the repository's Actions tab, enter a version, and it tests, builds and attaches
everything to a (by default draft) GitHub release:

| Build | Platforms |
|---|---|
| Windows app (`netDigger-app-*`) | win-x64, win-arm64 |
| Command-line tool (`netdigger-cli-*`) | win, linux, macOS, each x64 and arm64 |

Every build ships as `self-contained` (bundles .NET, nothing to install) and `framework-dependent` (small, needs the
.NET 8 runtime), plus a `SHA256SUMS.txt`.

## License

GNU Affero General Public License v3.0. See [LICENSE](LICENSE).
