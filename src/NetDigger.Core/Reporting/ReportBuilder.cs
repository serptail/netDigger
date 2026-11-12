using Microsoft.Data.Sqlite;
using NetDigger.Core.Storage;

namespace NetDigger.Core.Reporting;

/// <summary>Runs the analysis queries against a crawl database and assembles a <see cref="SiteReport"/>.</summary>
public static class ReportBuilder
{
    private const int Limit = 500;

    public static SiteReport Build(string databasePath)
    {
        using var connection = CrawlDatabase.OpenReadOnly(databasePath);
        return Build(connection);
    }

    public static SiteReport Build(SqliteConnection c)
    {
        var info = Query(c, "SELECT key, value FROM crawl_info", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(t => t.Item1, t => t.Item2);

        var issues = new List<PageIssue>();
        void Issues(string category, string sql) =>
            issues.AddRange(Query(c, sql, r => new PageIssue(category, r.GetString(0), r.GetNullableString(1))));

        Issues("Missing title", $"SELECT url, NULL FROM pages WHERE title IS NULL OR title = '' LIMIT {Limit}");
        Issues("Duplicate title",
            $"SELECT url, title FROM pages WHERE title IN (SELECT title FROM pages WHERE title <> '' GROUP BY title HAVING COUNT(*) > 1) ORDER BY title, url LIMIT {Limit}");
        Issues("Missing meta description", $"SELECT url, NULL FROM pages WHERE description IS NULL OR description = '' LIMIT {Limit}");
        Issues("Missing H1", $"SELECT url, NULL FROM pages WHERE h1_count = 0 LIMIT {Limit}");
        Issues("Multiple H1", $"SELECT url, h1_count || ' H1 elements' FROM pages WHERE h1_count > 1 LIMIT {Limit}");
        Issues("noindex", $"SELECT url, meta_robots FROM pages WHERE lower(meta_robots) LIKE '%noindex%' LIMIT {Limit}");
        Issues("Thin content", $"SELECT url, word_count || ' words' FROM pages WHERE word_count < 100 LIMIT {Limit}");
        Issues("Duplicate content",
            $"SELECT url, content_hash FROM pages WHERE content_hash <> '' AND content_hash IN (SELECT content_hash FROM pages WHERE content_hash <> '' GROUP BY content_hash HAVING COUNT(*) > 1) ORDER BY content_hash, url LIMIT {Limit}");

        var findings = new List<FindingSummary>();
        foreach (var category in Enum.GetNames<FindingCategory>())
        {
            var detailColumn = category is "Comment" or "HiddenElement" ? "MIN(url)" : "MIN(detail)";
            findings.AddRange(Query(c,
                $"SELECT category, value, COUNT(DISTINCT url), {detailColumn} FROM findings WHERE category = '{category}' GROUP BY value ORDER BY 3 DESC, value LIMIT 100",
                r => new FindingSummary(r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetNullableString(3))));
        }

        return new SiteReport
        {
            Info = info,
            TotalUrls = (int)Scalar(c, "SELECT COUNT(*) FROM resources"),
            InScopeUrls = (int)Scalar(c, "SELECT COUNT(*) FROM resources WHERE in_scope = 1"),
            PagesAnalysed = (int)Scalar(c, "SELECT COUNT(*) FROM pages"),
            ByKind = Counts(c, "SELECT kind, COUNT(*) FROM resources GROUP BY kind ORDER BY 2 DESC"),
            ByStatus = Counts(c,
                "SELECT CASE WHEN status IS NULL THEN outcome WHEN status < 300 THEN '2xx' WHEN status < 400 THEN '3xx' WHEN status < 500 THEN '4xx' ELSE '5xx' END AS c, COUNT(*) FROM resources GROUP BY c ORDER BY c"),
            ByHost = Counts(c, "SELECT host, COUNT(*) FROM resources GROUP BY host ORDER BY 2 DESC LIMIT 20"),
            BrokenLinks = Query(c,
                $"""
                SELECT r.url, r.status, r.error, r.kind, r.in_scope,
                       (SELECT COUNT(*) FROM links l WHERE l.target = r.url) AS inbound,
                       (SELECT l.source FROM links l WHERE l.target = r.url LIMIT 1) AS first_source
                FROM resources r
                WHERE r.status >= 400 OR r.outcome = 'Failed'
                ORDER BY r.in_scope DESC, inbound DESC
                LIMIT {Limit}
                """,
                r => new BrokenLink(
                    r.GetString(0), r.GetNullableInt(1), r.GetNullableString(2), r.GetString(3),
                    r.GetInt64(4) == 1, (int)r.GetInt64(5), r.GetNullableString(6))),
            Redirects = Query(c,
                $"SELECT url, redirect_to, status FROM resources WHERE redirect_to IS NOT NULL ORDER BY url LIMIT {Limit}",
                r => new RedirectItem(r.GetString(0), r.GetString(1), r.GetNullableInt(2))),
            PageIssues = issues,
            OrphanPages = Query(c,
                $"""
                SELECT DISTINCT l.target FROM links l
                JOIN resources r ON r.url = l.target
                WHERE l.type = 'Sitemap' AND r.kind = 'Page' AND r.discovered_via <> 'Seed'
                  AND NOT EXISTS (SELECT 1 FROM links a WHERE a.target = l.target AND a.type IN ('Anchor', 'Frame'))
                ORDER BY l.target LIMIT {Limit}
                """,
                r => r.GetString(0)),
            SlowestPages = Query(c,
                "SELECT url, response_ms, NULL FROM resources WHERE kind = 'Page' AND response_ms IS NOT NULL ORDER BY response_ms DESC LIMIT 10",
                r => new MetricItem(r.GetString(0), r.GetInt64(1), null)),
            LargestResources = Query(c,
                "SELECT url, size_bytes, kind FROM resources WHERE size_bytes IS NOT NULL ORDER BY size_bytes DESC LIMIT 10",
                r => new MetricItem(r.GetString(0), r.GetInt64(1), r.GetString(2))),
            SensitiveFiles = Query(c,
                $"""
                SELECT url, kind FROM resources
                WHERE in_scope = 1 AND (kind IN ('Database', 'Archive')
                   OR lower(url) LIKE '%.bak' OR lower(url) LIKE '%.old' OR lower(url) LIKE '%.orig' OR lower(url) LIKE '%.swp'
                   OR lower(url) LIKE '%/.env' OR lower(url) LIKE '%/.git/%' OR lower(url) LIKE '%/.svn/%' OR lower(url) LIKE '%/.ds_store')
                ORDER BY url LIMIT {Limit}
                """,
                r => new PageIssue("Potentially sensitive file", r.GetString(0), r.GetString(1))),
            Findings = findings,
            Hosts = Query(c,
                "SELECT host, server, powered_by, tls_subject, tls_issuer, tls_not_after, tls_error, missing_security_headers FROM hosts ORDER BY host",
                r => new HostInfo(
                    r.GetString(0), r.GetNullableString(1), r.GetNullableString(2), r.GetNullableString(3),
                    r.GetNullableString(4), r.GetNullableString(5), r.GetNullableString(6),
                    (r.GetNullableString(7) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)))
        };
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static List<CountItem> Counts(SqliteConnection c, string sql) =>
        Query(c, sql, r => new CountItem(r.GetString(0), (int)r.GetInt64(1)));

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
}
