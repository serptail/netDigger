using System.IO;

namespace netDigger
{
    public class ExportHandler
    {
        // SaveUrlsToTxtAsync method: Saves URLs to a text file asynchronously
        public async Task SaveUrlsToTxtAsync(List<string> urls)
        {
            string txtFilePath = "ScrapedUrls.txt";
            using (var writer = new StreamWriter(txtFilePath))
            {
                foreach (var url in urls)
                {
                    await writer.WriteLineAsync(url);
                }
            }
        }

        // SaveUrlsToCsvAsync method: Saves URLs to a CSV file asynchronously
        public async Task SaveUrlsToCsvAsync(List<string> urls)
        {
            string csvFilePath = "ScrapedUrls.csv";
            using (var writer = new StreamWriter(csvFilePath))
            {
                await writer.WriteLineAsync("Url"); // Write header row
                foreach (var url in urls)
                {
                    await writer.WriteLineAsync(url); // Write each URL on a new line
                }
            }
        }
    }
}