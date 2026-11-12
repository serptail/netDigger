using Microsoft.Data.Sqlite;

namespace NetDigger.Core.Storage;

/// <summary>Schema definition and helpers for opening netDigger crawl databases.</summary>
public static class CrawlDatabase
{
    public const int SchemaVersion = 1;

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS crawl_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS resources (
            url            TEXT PRIMARY KEY,
            host           TEXT NOT NULL,
            kind           TEXT NOT NULL,
            outcome        TEXT NOT NULL,
            in_scope       INTEGER NOT NULL,
            depth          INTEGER NOT NULL,
            discovered_via TEXT NOT NULL,
            referrer       TEXT,
            status         INTEGER,
            content_type   TEXT,
            size_bytes     INTEGER,
            response_ms    INTEGER,
            redirect_to    TEXT,
            error          TEXT,
            recorded_at    TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS pages (
            url          TEXT PRIMARY KEY,
            title        TEXT,
            description  TEXT,
            canonical    TEXT,
            language     TEXT,
            meta_robots  TEXT,
            generator    TEXT,
            first_h1     TEXT,
            h1_count     INTEGER NOT NULL,
            h2_count     INTEGER NOT NULL,
            word_count   INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            open_graph   TEXT,
            twitter_card TEXT
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS links (
            source   TEXT NOT NULL,
            target   TEXT NOT NULL,
            type     TEXT NOT NULL,
            anchor   TEXT,
            nofollow INTEGER NOT NULL,
            PRIMARY KEY (source, target, type)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS findings (
            url      TEXT NOT NULL,
            category TEXT NOT NULL,
            value    TEXT NOT NULL,
            detail   TEXT,
            PRIMARY KEY (url, category, value)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS hosts (
            host                     TEXT PRIMARY KEY,
            server                   TEXT,
            powered_by               TEXT,
            tls_subject              TEXT,
            tls_issuer               TEXT,
            tls_not_after            TEXT,
            tls_error                TEXT,
            missing_security_headers TEXT
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_links_target     ON links(target);
        CREATE INDEX IF NOT EXISTS idx_resources_kind   ON resources(kind);
        CREATE INDEX IF NOT EXISTS idx_resources_status ON resources(status);
        CREATE INDEX IF NOT EXISTS idx_findings_cat     ON findings(category);
        """;

    /// <summary>Opens an existing crawl database for reading.</summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Crawl database not found: {path}", path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

internal static class SqliteReaderExtensions
{
    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int? GetNullableInt(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (int)reader.GetInt64(ordinal);

    public static long? GetNullableLong(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
