using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetDigger.Core.Storage;

namespace NetDigger.Core.Reporting;

/// <summary>Writes crawl results out of the database as CSV, JSON, plain URL lists and an HTML report.</summary>
public static class CrawlExporter
{
    private static readonly string[] ResourceColumns =
    {
        "url", "host", "kind", "outcome", "in_scope", "depth", "discovered_via", "referrer", "status",
        "content_type", "size_bytes", "response_ms", "redirect_to", "error"
    };

    public static void ExportResourcesCsv(string databasePath, string outputPath)
    {
        using var connection = CrawlDatabase.OpenReadOnly(databasePath);
        EnsureDirectory(outputPath);

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(',', ResourceColumns));

        using var reader = SelectResources(connection);
        var fields = new string[ResourceColumns.Length];
        while (reader.Read())
        {
            for (var i = 0; i < fields.Length; i++)
                fields[i] = CsvEscape(reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i)) ?? string.Empty);
            writer.WriteLine(string.Join(',', fields));
        }
    }

    public static void ExportResourcesJson(string databasePath, string outputPath)
    {
        using var connection = CrawlDatabase.OpenReadOnly(databasePath);
        EnsureDirectory(outputPath);

        using var stream = File.Create(outputPath);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        using var reader = SelectResources(connection);

        json.WriteStartArray();
        while (reader.Read())
        {
            json.WriteStartObject();
            for (var i = 0; i < ResourceColumns.Length; i++)
            {
                json.WritePropertyName(ResourceColumns[i]);
                if (reader.IsDBNull(i)) json.WriteNullValue();
                else if (reader.GetFieldType(i) == typeof(long)) json.WriteNumberValue(reader.GetInt64(i));
                else json.WriteStringValue(reader.GetString(i));
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>Writes one URL per line, optionally restricted to a kind and/or in-scope resources.</summary>
    public static int ExportUrlList(string databasePath, string outputPath, ResourceKind? kind = null, bool inScopeOnly = false)
    {
        using var connection = CrawlDatabase.OpenReadOnly(databasePath);
        EnsureDirectory(outputPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url FROM resources WHERE ($kind IS NULL OR kind = $kind) AND ($scope = 0 OR in_scope = 1) ORDER BY url";
        command.Parameters.AddWithValue("$kind", kind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$scope", inScopeOnly ? 1 : 0);

        var count = 0;
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            writer.WriteLine(reader.GetString(0));
            count++;
        }

        return count;
    }

    /// <summary>Writes pdf.txt, image.txt, ... into <paramref name="directory"/>, one file per kind found.</summary>
    public static int ExportUrlsByKind(string databasePath, string directory)
    {
        List<string> kinds;
        using (var connection = CrawlDatabase.OpenReadOnly(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT kind FROM resources ORDER BY kind";
            using var reader = command.ExecuteReader();
            kinds = new List<string>();
            while (reader.Read()) kinds.Add(reader.GetString(0));
        }

        Directory.CreateDirectory(directory);
        foreach (var kind in kinds)
        {
            if (Enum.TryParse<ResourceKind>(kind, out var parsed))
                ExportUrlList(databasePath, Path.Combine(directory, kind.ToLowerInvariant() + ".txt"), parsed);
        }

        return kinds.Count;
    }

    public static void ExportReportHtml(string databasePath, string outputPath)
    {
        EnsureDirectory(outputPath);
        File.WriteAllText(outputPath, HtmlReportWriter.Render(ReportBuilder.Build(databasePath)), new UTF8Encoding(false));
    }

    public static void ExportReportJson(string databasePath, string outputPath)
    {
        EnsureDirectory(outputPath);
        var json = JsonSerializer.Serialize(ReportBuilder.Build(databasePath), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
    }

    internal static string CsvEscape(string value)
    {
        // Neutralise spreadsheet formula injection from attacker-controlled text.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static SqliteDataReader SelectResources(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", ResourceColumns)} FROM resources ORDER BY url";
        return command.ExecuteReader();
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }
}
