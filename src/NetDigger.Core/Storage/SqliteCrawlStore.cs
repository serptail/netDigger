using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace NetDigger.Core.Storage;

/// <summary>
/// Persists crawl records to a single SQLite file. Writes are queued and committed in batches by one
/// background writer, so the crawl never blocks on disk for individual rows (but is back-pressured if the
/// disk cannot keep up).
/// </summary>
public sealed class SqliteCrawlStore : ICrawlSink, IAsyncDisposable
{
    private const int BatchSize = 500;
    private const int QueueCapacity = 20_000;

    private readonly SqliteConnection _connection;
    private readonly Channel<CrawlRecord> _queue;
    private readonly Task _writer;

    private readonly SqliteCommand _insertResource;
    private readonly SqliteCommand _insertPage;
    private readonly SqliteCommand _insertLink;
    private readonly SqliteCommand _insertFinding;
    private readonly SqliteCommand _insertHost;
    private readonly SqliteCommand _insertInfo;
    private readonly SqliteCommand[] _commands;

    private SqliteCrawlStore(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
        _queue = Channel.CreateBounded<CrawlRecord>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        _insertResource = Prepare(
            """
            INSERT OR REPLACE INTO resources
              (url, host, kind, outcome, in_scope, depth, discovered_via, referrer, status, content_type,
               size_bytes, response_ms, redirect_to, error, recorded_at)
            VALUES
              ($url, $host, $kind, $outcome, $in_scope, $depth, $via, $referrer, $status, $content_type,
               $size, $ms, $redirect, $error, $at)
            """,
            "$url", "$host", "$kind", "$outcome", "$in_scope", "$depth", "$via", "$referrer", "$status",
            "$content_type", "$size", "$ms", "$redirect", "$error", "$at");

        _insertPage = Prepare(
            """
            INSERT OR REPLACE INTO pages
              (url, title, description, canonical, language, meta_robots, generator, first_h1,
               h1_count, h2_count, word_count, content_hash, open_graph, twitter_card)
            VALUES
              ($url, $title, $description, $canonical, $language, $robots, $generator, $h1,
               $h1_count, $h2_count, $words, $hash, $og, $twitter)
            """,
            "$url", "$title", "$description", "$canonical", "$language", "$robots", "$generator", "$h1",
            "$h1_count", "$h2_count", "$words", "$hash", "$og", "$twitter");

        _insertLink = Prepare(
            "INSERT OR IGNORE INTO links (source, target, type, anchor, nofollow) VALUES ($source, $target, $type, $anchor, $nofollow)",
            "$source", "$target", "$type", "$anchor", "$nofollow");

        _insertFinding = Prepare(
            "INSERT OR IGNORE INTO findings (url, category, value, detail) VALUES ($url, $category, $value, $detail)",
            "$url", "$category", "$value", "$detail");

        _insertHost = Prepare(
            """
            INSERT OR REPLACE INTO hosts
              (host, server, powered_by, tls_subject, tls_issuer, tls_not_after, tls_error, missing_security_headers)
            VALUES
              ($host, $server, $powered, $subject, $issuer, $not_after, $tls_error, $missing)
            """,
            "$host", "$server", "$powered", "$subject", "$issuer", "$not_after", "$tls_error", "$missing");

        _insertInfo = Prepare(
            "INSERT OR REPLACE INTO crawl_info (key, value) VALUES ($key, $value)",
            "$key", "$value");

        _commands = new[] { _insertResource, _insertPage, _insertLink, _insertFinding, _insertHost, _insertInfo };
        _writer = Task.Run(WriterLoopAsync);
    }

    /// <summary>Location of the database file.</summary>
    public string Path { get; }

    /// <summary>Creates a new crawl database. Fails if the file exists unless <paramref name="overwrite"/> is set.</summary>
    public static SqliteCrawlStore Create(string path, bool overwrite = false)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(fullPath))
        {
            if (!overwrite) throw new IOException($"'{fullPath}' already exists.");
            File.Delete(fullPath);
            TryDelete(fullPath + "-wal");
            TryDelete(fullPath + "-shm");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                setup.ExecuteNonQuery();
                setup.CommandText = CrawlDatabase.Schema;
                setup.ExecuteNonQuery();
                setup.CommandText = $"PRAGMA user_version = {CrawlDatabase.SchemaVersion};";
                setup.ExecuteNonQuery();
            }

            return new SqliteCrawlStore(fullPath, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public ValueTask WriteAsync(CrawlRecord record, CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(record, cancellationToken);

    /// <summary>Stops accepting records, waits for everything queued to be committed, and finalises the file.</summary>
    public async ValueTask CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A writer failure has already been surfaced to callers of WriteAsync/CompleteAsync.
        }
        finally
        {
            foreach (var command in _commands) command.Dispose();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------------------

    private async Task WriterLoopAsync()
    {
        var batch = new List<CrawlRecord>(BatchSize);
        var reader = _queue.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < BatchSize && reader.TryRead(out var record)) batch.Add(record);

                Commit(batch);
                batch.Clear();
            }

            Finalise();
        }
        catch (Exception ex)
        {
            // Unblock producers: further writes will fail with this exception instead of waiting forever.
            _queue.Writer.TryComplete(ex);
            throw;
        }
    }

    private void Commit(List<CrawlRecord> batch)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var command in _commands) command.Transaction = transaction;

        foreach (var record in batch)
        {
            switch (record)
            {
                case ResourceRecord r: Insert(r); break;
                case PageRecord p: Insert(p); break;
                case LinkRecord l: Insert(l); break;
                case FindingRecord f: Insert(f); break;
                case HostRecord h: Insert(h); break;
                case InfoRecord i: Insert(i); break;
            }
        }

        transaction.Commit();
    }

    /// <summary>Folds the WAL back into the main file so the result is one self-contained database.</summary>
    private void Finalise()
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE;";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The data is already committed; leaving the journal in WAL mode is harmless.
        }
    }

    private void Insert(ResourceRecord r)
    {
        var p = _insertResource.Parameters;
        p["$url"].Value = r.Url;
        p["$host"].Value = r.Host;
        p["$kind"].Value = r.Kind.ToString();
        p["$outcome"].Value = r.Outcome.ToString();
        p["$in_scope"].Value = r.InScope ? 1 : 0;
        p["$depth"].Value = r.Depth;
        p["$via"].Value = r.DiscoveredVia.ToString();
        p["$referrer"].Value = Db(r.Referrer);
        p["$status"].Value = Db(r.StatusCode);
        p["$content_type"].Value = Db(r.ContentType);
        p["$size"].Value = Db(r.SizeBytes);
        p["$ms"].Value = Db(r.ResponseMs);
        p["$redirect"].Value = Db(r.RedirectTo);
        p["$error"].Value = Db(r.Error);
        p["$at"].Value = r.RecordedAt.ToString("O");
        _insertResource.ExecuteNonQuery();
    }

    private void Insert(PageRecord r)
    {
        var p = _insertPage.Parameters;
        p["$url"].Value = r.Url;
        p["$title"].Value = Db(r.Title);
        p["$description"].Value = Db(r.Description);
        p["$canonical"].Value = Db(r.Canonical);
        p["$language"].Value = Db(r.Language);
        p["$robots"].Value = Db(r.MetaRobots);
        p["$generator"].Value = Db(r.Generator);
        p["$h1"].Value = Db(r.FirstHeading);
        p["$h1_count"].Value = r.H1Count;
        p["$h2_count"].Value = r.H2Count;
        p["$words"].Value = r.WordCount;
        p["$hash"].Value = r.ContentHash;
        p["$og"].Value = r.OpenGraph.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(r.OpenGraph);
        p["$twitter"].Value = r.TwitterCard.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(r.TwitterCard);
        _insertPage.ExecuteNonQuery();
    }

    private void Insert(LinkRecord r)
    {
        var p = _insertLink.Parameters;
        p["$source"].Value = r.Source;
        p["$target"].Value = r.Target;
        p["$type"].Value = r.Type.ToString();
        p["$anchor"].Value = Db(r.Anchor);
        p["$nofollow"].Value = r.NoFollow ? 1 : 0;
        _insertLink.ExecuteNonQuery();
    }

    private void Insert(FindingRecord r)
    {
        var p = _insertFinding.Parameters;
        p["$url"].Value = r.Url;
        p["$category"].Value = r.Category.ToString();
        p["$value"].Value = r.Value;
        p["$detail"].Value = Db(r.Detail);
        _insertFinding.ExecuteNonQuery();
    }

    private void Insert(HostRecord r)
    {
        var p = _insertHost.Parameters;
        p["$host"].Value = r.Host;
        p["$server"].Value = Db(r.Server);
        p["$powered"].Value = Db(r.PoweredBy);
        p["$subject"].Value = Db(r.TlsSubject);
        p["$issuer"].Value = Db(r.TlsIssuer);
        p["$not_after"].Value = r.TlsNotAfter is { } notAfter ? notAfter.ToString("O") : DBNull.Value;
        p["$tls_error"].Value = Db(r.TlsError);
        p["$missing"].Value = string.Join(',', r.MissingSecurityHeaders);
        _insertHost.ExecuteNonQuery();
    }

    private void Insert(InfoRecord r)
    {
        var p = _insertInfo.Parameters;
        p["$key"].Value = r.Key;
        p["$value"].Value = r.Value;
        _insertInfo.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameterNames)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var name in parameterNames)
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        return command;
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Stale sidecar files are harmless.
        }
    }
}
