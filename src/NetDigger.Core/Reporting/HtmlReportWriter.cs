using System.Net;
using System.Text;

namespace NetDigger.Core.Reporting;

/// <summary>Renders a <see cref="SiteReport"/> as a single self-contained HTML file.</summary>
public static class HtmlReportWriter
{
    private const string Style = """
        :root{--bg:#fff;--fg:#1a1d21;--muted:#6b7280;--line:#e5e7eb;--card:#f8fafc;--accent:#2563eb;--bad:#dc2626}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1115;--fg:#e5e7eb;--muted:#9ca3af;--line:#272b33;--card:#171a20;--accent:#60a5fa;--bad:#f87171}}
        body{font:14px/1.5 system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:2rem;max-width:1100px;margin-inline:auto}
        h1{margin:0 0 .25rem}h2{margin-top:2.5rem;border-bottom:1px solid var(--line);padding-bottom:.3rem}
        .muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:.75rem;margin:1rem 0}
        .card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:.75rem 1rem}
        .card b{display:block;font-size:1.6rem}table{border-collapse:collapse;width:100%;margin:.5rem 0}
        th,td{text-align:left;padding:.35rem .6rem;border-bottom:1px solid var(--line);vertical-align:top;word-break:break-all}
        th{color:var(--muted);font-weight:600}a{color:var(--accent);text-decoration:none}.bad{color:var(--bad)}
        """;

    public static string Render(SiteReport report)
    {
        var sb = new StringBuilder();
        report.Info.TryGetValue("seeds", out var seeds);
        report.Info.TryGetValue("status", out var status);

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>netDigger report</title><style>").Append(Style).Append("</style></head><body>");
        sb.Append("<h1>netDigger report</h1><p class=\"muted\">")
          .Append(E(seeds?.Replace("\n", ", "))).Append(" · status: ").Append(E(status ?? "unknown"))
          .Append(report.Info.TryGetValue("finished_at", out var done) ? " · finished " + E(done) : string.Empty)
          .Append("</p>");

        sb.Append("<div class=\"grid\">");
        Card(sb, "URLs discovered", report.TotalUrls);
        Card(sb, "In scope", report.InScopeUrls);
        Card(sb, "Pages analysed", report.PagesAnalysed);
        Card(sb, "Broken links", report.BrokenLinks.Count);
        Card(sb, "Orphan pages", report.OrphanPages.Count);
        sb.Append("</div>");

        Section(sb, "Resources by type", report.ByKind.Select(i => new[] { i.Label, i.Count.ToString() }), "Type", "Count");
        Section(sb, "Responses", report.ByStatus.Select(i => new[] { i.Label, i.Count.ToString() }), "Status", "Count");
        Section(sb, "Hosts", report.ByHost.Select(i => new[] { i.Label, i.Count.ToString() }), "Host", "URLs");

        Section(sb, "Broken links", report.BrokenLinks.Select(b => new[]
        {
            Link(b.Url), b.Status?.ToString() ?? E(b.Error), b.InScope ? "internal" : "external",
            b.InboundLinks.ToString(), Link(b.FirstReferrer)
        }), "URL", "Status / error", "Where", "Inbound", "Found on");

        Section(sb, "Redirects", report.Redirects.Select(r => new[] { Link(r.Url), (r.Status?.ToString() ?? ""), Link(r.Target) }),
            "From", "Status", "To");

        Section(sb, "Page issues", report.PageIssues.Select(p => new[] { E(p.Category), Link(p.Url), E(p.Detail) }),
            "Issue", "Page", "Detail");

        Section(sb, "Orphan pages (in sitemap, never linked)", report.OrphanPages.Select(o => new[] { Link(o) }), "Page");
        Section(sb, "Potentially sensitive files", report.SensitiveFiles.Select(s => new[] { Link(s.Url), E(s.Detail) }), "URL", "Type");

        foreach (var group in report.Findings.GroupBy(f => f.Category))
        {
            Section(sb, $"Findings: {group.Key}",
                group.Select(f => new[] { E(f.Value), f.Pages.ToString(), E(f.Detail) }), "Value", "Pages", "Detail");
        }

        Section(sb, "Hosts and security headers", report.Hosts.Select(h => new[]
        {
            E(h.Host), E(h.Server ?? h.PoweredBy),
            E(h.TlsError is null ? h.TlsNotAfter : $"{h.TlsNotAfter} ({h.TlsError})"),
            h.MissingSecurityHeaders.Count == 0 ? "none" : E(string.Join(", ", h.MissingSecurityHeaders))
        }), "Host", "Server", "Certificate expires", "Missing headers");

        Section(sb, "Slowest pages (ms)", report.SlowestPages.Select(m => new[] { Link(m.Url), m.Value.ToString() }), "Page", "ms");
        Section(sb, "Largest resources", report.LargestResources.Select(m => new[] { Link(m.Url), FormatBytes(m.Value), E(m.Extra) }),
            "URL", "Size", "Type");

        return sb.Append("</body></html>").ToString();
    }

    // Cells passed to Section are already HTML-safe (built with E/Link).
    private static void Section(StringBuilder sb, string title, IEnumerable<string[]> rows, params string[] headers)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        sb.Append("<h2>").Append(E(title)).Append(" <span class=\"muted\">(").Append(list.Count).Append(")</span></h2><table><tr>");
        foreach (var header in headers) sb.Append("<th>").Append(E(header)).Append("</th>");
        sb.Append("</tr>");
        foreach (var row in list)
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append("<td>").Append(cell).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");
    }

    private static void Card(StringBuilder sb, string label, int value) =>
        sb.Append("<div class=\"card\"><b>").Append(value.ToString("N0")).Append("</b>").Append(E(label)).Append("</div>");

    private static string Link(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        var safe = E(url);
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? $"<a href=\"{safe}\" rel=\"noopener noreferrer\">{safe}</a>"
            : safe;
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
