using System.Data.SQLite;
using System.IO;

namespace netDigger
{
    public class DataProcessor
    {
        private string _baseDirectory;
        private List<string> _selectedDataTypes;
        private string _selectedPath;

        public DataProcessor()
        {
            // Initialize base directory for databases
            _baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Databases");
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
            }
        }

        // Initialize method: Sets selected data types and path
        public void Initialize(List<string> selectedDataTypes, string selectedPath)
        {
            _selectedDataTypes = selectedDataTypes;
            _selectedPath = selectedPath;
        }

        // OrganizeUrls method: Categorizes URLs and creates databases accordingly
        public async Task OrganizeUrlsAsync(List<string> urls)
        {
            var categorizedUrls = CategorizeUrls(urls);
            var tasks = new List<Task>();

            foreach (var category in categorizedUrls.Keys)
            {
                // Create database for each category of URLs
                if (_selectedDataTypes.Contains(category) || category == "Miscellaneous")
                {
                    var databasePath = Path.Combine(_selectedPath, $"{category}Database.db");
                    tasks.Add(CreateDatabaseAsync(databasePath, categorizedUrls[category]));
                }
            }

            await Task.WhenAll(tasks);
        }

        // CategorizeUrls method: Groups URLs by file extension categories
        private Dictionary<string, List<string>> CategorizeUrls(List<string> urls)
        {
            var categories = new Dictionary<string, List<string>>
            {
                // Initialize categories
                { "PDF", new List<string>() },
                { "CSV", new List<string>() },
                { "DOCX", new List<string>() },
                { "XLS", new List<string>() },
                { "PPTX", new List<string>() },
                { "TXT", new List<string>() },
                { "Images", new List<string>() },
                { "Videos", new List<string>() },
                { "JSON", new List<string>() },
                { "DBSQL", new List<string>() },
                { "XML", new List<string>() },
                { "HTML", new List<string>() },
                { "PHP", new List<string>() },
                { "JS", new List<string>() },
                { "Archives", new List<string>() },
                { "Miscellaneous", new List<string>() }
            };

            // Categorize URLs based on file extensions
            foreach (var url in urls)
            {
                var extension = Path.GetExtension(url)?.ToLower();
                switch (extension)
                {
                    case ".pdf":
                        categories["PDF"].Add(url);
                        break;
                    case ".csv":
                        categories["CSV"].Add(url);
                        break;
                    case ".jpg":
                    case ".jpeg":
                    case ".png":
                    case ".svg":
                    case ".gif":
                        categories["Images"].Add(url);
                        break;
                    case ".json":
                        categories["JSON"].Add(url);
                        break;
                    case ".db":
                    case ".sql":
                    case ".sqlite":
                    case ".frm":
                    case ".ibd":
                    case ".myd":
                    case ".myi":
                    case ".ns":
                    case ".0":
                    case ".1":
                    case ".db3":
                    case ".rdb":
                    case ".plocal":
                    case ".couch":
                    case ".hfile":
                        categories["DBSQL"].Add(url);
                        break;
                    case ".xml":
                    case ".css":
                        categories["XML"].Add(url);
                        break;
                    case ".html":
                    case ".htm":
                    case ".xhtml":
                        categories["HTML"].Add(url);
                        break;
                    case ".php":
                        categories["PHP"].Add(url);
                        break;
                    case ".js":
                        categories["JS"].Add(url);
                        break;
                    case ".docx":
                        categories["DOCX"].Add(url);
                        break;
                    case ".pptx":
                        categories["PPTX"].Add(url);
                        break;
                    case ".xls":
                    case ".xlsx":
                        categories["XLS"].Add(url);
                        break;
                    case ".zip":
                    case ".gz":
                    case ".tar":
                        categories["Archives"].Add(url);
                        break;
                    case ".mp3":
                    case ".mp4":
                    case ".mkv":
                    case ".wav":
                        categories["Videos"].Add(url);
                        break;
                    case ".txt":
                        categories["TXT"].Add(url);
                        break;
                    default:
                        categories["Miscellaneous"].Add(url);
                        break;
                }
            }

            return categories;
        }

        // CreateDatabase method: Creates SQLite database and inserts URLs
        private async Task CreateDatabaseAsync(string databasePath, List<string> urls)
        {
            using (var connection = new SQLiteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();

                // Create table if not exists
                var createTableQuery = @"CREATE TABLE IF NOT EXISTS ScrapedUrls (
                                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                            Url TEXT NOT NULL,
                                            ScrapeDate DATETIME DEFAULT CURRENT_TIMESTAMP
                                        );";
                using (var command = new SQLiteCommand(createTableQuery, connection))
                {
                    await command.ExecuteNonQueryAsync();
                }

                // Insert URLs into the database in batches
                int batchSize = 100;
                for (int i = 0; i < urls.Count; i += batchSize)
                {
                    var batch = urls.Skip(i).Take(batchSize).ToList();
                    var insertQuery = "INSERT INTO ScrapedUrls (Url) VALUES (@url)";

                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var url in batch)
                        {
                            using (var command = new SQLiteCommand(insertQuery, connection, transaction))
                            {
                                command.Parameters.AddWithValue("@url", url);
                                await command.ExecuteNonQueryAsync();
                            }
                        }
                        transaction.Commit();
                    }
                }
            }
        }
    }
}