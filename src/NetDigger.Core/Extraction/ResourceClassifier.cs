namespace NetDigger.Core.Extraction;

/// <summary>Classifies URLs by extension and responses by content type.</summary>
public static class ResourceClassifier
{
    private static readonly Dictionary<string, ResourceKind> ByExtension = BuildExtensionMap();

    /// <summary>
    /// Classifies by the extension of the URL path only (never the query string).
    /// Extensionless and unrecognised paths are treated as pages; the response content type has the final say.
    /// </summary>
    public static ResourceKind FromUrl(Uri url)
    {
        var ext = Path.GetExtension(url.AbsolutePath);
        if (string.IsNullOrEmpty(ext)) return ResourceKind.Page;
        return ByExtension.TryGetValue(ext, out var kind) ? kind : ResourceKind.Page;
    }

    /// <summary>Classifies by media type. Returns null when the type is missing or not recognised.</summary>
    public static ResourceKind? FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var ct = contentType.ToLowerInvariant();

        if (ContentTypes.IsHtml(ct)) return ResourceKind.Page;
        if (ContentTypes.IsJson(ct)) return ResourceKind.Json;
        if (ContentTypes.IsCss(ct)) return ResourceKind.Stylesheet;
        if (ContentTypes.IsJavaScript(ct)) return ResourceKind.Script;
        if (ContentTypes.IsXml(ct)) return ResourceKind.Xml;
        if (ct.StartsWith("image/", StringComparison.Ordinal)) return ResourceKind.Image;
        if (ct.StartsWith("video/", StringComparison.Ordinal)) return ResourceKind.Video;
        if (ct.StartsWith("audio/", StringComparison.Ordinal)) return ResourceKind.Audio;
        if (ct.StartsWith("font/", StringComparison.Ordinal) || ct.Contains("font-woff", StringComparison.Ordinal)
            || ct.Contains("opentype", StringComparison.Ordinal) || ct.Contains("truetype", StringComparison.Ordinal))
            return ResourceKind.Font;
        if (ct == "application/pdf") return ResourceKind.Pdf;
        if (ct == "text/csv") return ResourceKind.Csv;
        if (ct == "text/plain") return ResourceKind.Text;
        if (ct.Contains("wordprocessingml", StringComparison.Ordinal) || ct == "application/msword"
            || ct == "application/rtf" || ct.Contains("opendocument.text", StringComparison.Ordinal))
            return ResourceKind.Document;
        if (ct.Contains("spreadsheetml", StringComparison.Ordinal) || ct == "application/vnd.ms-excel"
            || ct.Contains("opendocument.spreadsheet", StringComparison.Ordinal))
            return ResourceKind.Spreadsheet;
        if (ct.Contains("presentationml", StringComparison.Ordinal) || ct == "application/vnd.ms-powerpoint"
            || ct.Contains("opendocument.presentation", StringComparison.Ordinal))
            return ResourceKind.Presentation;
        if (ct is "application/zip" or "application/gzip" or "application/x-gzip" or "application/x-tar"
            or "application/x-7z-compressed" or "application/vnd.rar" or "application/x-rar-compressed"
            or "application/x-bzip2" or "application/x-xz")
            return ResourceKind.Archive;
        if (ct is "application/x-sqlite3" or "application/vnd.sqlite3" or "application/sql")
            return ResourceKind.Database;

        return null;
    }

    /// <summary>Kinds whose bodies are worth downloading and mining for further URLs.</summary>
    public static bool IsTraversable(ResourceKind kind, bool includeScripts) => kind switch
    {
        ResourceKind.Page or ResourceKind.Stylesheet or ResourceKind.Json or ResourceKind.Xml => true,
        ResourceKind.Script => includeScripts,
        _ => false
    };

    private static Dictionary<string, ResourceKind> BuildExtensionMap()
    {
        var map = new Dictionary<string, ResourceKind>(StringComparer.OrdinalIgnoreCase);

        void Add(ResourceKind kind, params string[] extensions)
        {
            foreach (var ext in extensions) map["." + ext] = kind;
        }

        Add(ResourceKind.Page, "html", "htm", "xhtml", "shtml", "php", "asp", "aspx", "jsp", "jspx", "cfm");
        Add(ResourceKind.Pdf, "pdf");
        Add(ResourceKind.Csv, "csv", "tsv");
        Add(ResourceKind.Document, "doc", "docx", "odt", "rtf", "pages");
        Add(ResourceKind.Spreadsheet, "xls", "xlsx", "xlsm", "ods", "numbers");
        Add(ResourceKind.Presentation, "ppt", "pptx", "odp", "key");
        Add(ResourceKind.Text, "txt", "md", "log", "ini", "conf", "yml", "yaml");
        Add(ResourceKind.Image, "jpg", "jpeg", "png", "gif", "svg", "webp", "avif", "ico", "bmp", "tif", "tiff", "heic");
        Add(ResourceKind.Video, "mp4", "mkv", "webm", "mov", "avi", "m4v", "wmv", "flv");
        Add(ResourceKind.Audio, "mp3", "wav", "ogg", "flac", "m4a", "aac", "opus");
        Add(ResourceKind.Json, "json", "jsonld", "geojson", "webmanifest");
        Add(ResourceKind.Xml, "xml", "rss", "atom", "xsl", "xslt", "kml");
        Add(ResourceKind.Stylesheet, "css");
        Add(ResourceKind.Script, "js", "mjs", "map");
        Add(ResourceKind.Font, "woff", "woff2", "ttf", "otf", "eot");
        Add(ResourceKind.Database, "db", "sql", "sqlite", "sqlite3", "db3", "mdb", "accdb", "frm", "ibd", "myd", "myi", "rdb");
        Add(ResourceKind.Archive, "zip", "gz", "tgz", "tar", "7z", "rar", "bz2", "xz");

        return map;
    }
}

/// <summary>Helpers for reasoning about media types.</summary>
public static class ContentTypes
{
    public static bool IsHtml(string? ct) =>
        ct is not null && (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                           || ct.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase));

    public static bool IsJson(string? ct) =>
        ct is not null && (ct.Contains("/json", StringComparison.OrdinalIgnoreCase)
                           || ct.Contains("+json", StringComparison.OrdinalIgnoreCase));

    public static bool IsCss(string? ct) =>
        ct is not null && ct.StartsWith("text/css", StringComparison.OrdinalIgnoreCase);

    public static bool IsJavaScript(string? ct) =>
        ct is not null && (ct.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                           || ct.Contains("ecmascript", StringComparison.OrdinalIgnoreCase));

    /// <summary>XML in any of its media types, excluding XHTML (which is treated as HTML).</summary>
    public static bool IsXml(string? ct) =>
        ct is not null && !IsHtml(ct) && ct.Contains("xml", StringComparison.OrdinalIgnoreCase);

    public static bool IsPlainText(string? ct) =>
        ct is not null && ct.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the engine knows how to extract anything from a body of this type.</summary>
    public static bool IsParseable(string? ct, bool allowGzipFile = false)
    {
        if (ct is null) return false;
        if (IsHtml(ct) || IsXml(ct) || IsJson(ct) || IsCss(ct) || IsJavaScript(ct) || IsPlainText(ct)) return true;
        return allowGzipFile && (ct.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                                 || ct.Contains("octet-stream", StringComparison.OrdinalIgnoreCase));
    }
}
