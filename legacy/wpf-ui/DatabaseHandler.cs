using System.Data.SQLite;

namespace netDigger
{
    public class DatabaseHandler
    {
        private readonly string _connectionString = "Data Source=ScrapedUrls.db";

        // InitializeDatabase method: Creates the 'ScrapedUrls' table if it doesn't exist
        public void InitializeDatabase()
        {
            string createTableQuery = @"CREATE TABLE IF NOT EXISTS ScrapedUrls (
                                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                        Url TEXT NOT NULL,
                                        ScrapeDate DATETIME DEFAULT CURRENT_TIMESTAMP
                                    );";

            using (var connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SQLiteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        // SaveUrlsToDatabase method: Inserts URLs into the 'ScrapedUrls' table
        public void SaveUrlsToDatabase(List<string> urls)
        {
            using (var connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                foreach (var url in urls)
                {
                    var insertQuery = "INSERT INTO ScrapedUrls (Url) VALUES (@url)";
                    using (var command = new SQLiteCommand(insertQuery, connection))
                    {
                        command.Parameters.AddWithValue("@url", url);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }
    }
}