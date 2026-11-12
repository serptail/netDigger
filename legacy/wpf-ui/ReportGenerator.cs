using System.Text;
using System.IO;

namespace netDigger
{
    public class ReportGenerator
    {
        public string GenerateSummaryReport(List<string> data)
        {
            var report = new StringBuilder();
            report.AppendLine("Scraping Summary Report");
            report.AppendLine("========================");
            report.AppendLine($"Total URLs Scraped: {data.Count}");
            report.AppendLine($"First URL: {data.FirstOrDefault()}");
            report.AppendLine($"Last URL: {data.LastOrDefault()}");
            var uniqueDomains = data.Select(url => new Uri(url).Host).Distinct().Count();
            report.AppendLine($"Unique Domains: {uniqueDomains}");
            return report.ToString();
        }

        public void ExportReportToFile(string report, string filePath)
        {
            File.WriteAllText(filePath, report);
        }
    }
}
