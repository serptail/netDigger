using System.Windows;
using System.Windows.Media;

namespace netDigger
{
    public partial class MainWindow : Window
    {
        // Handlers and utilities
        private readonly DatabaseHandler _databaseHandler;
        private readonly Scraper _scraper;
        private readonly UrlDetailsFetcher _urlDetailsFetcher;
        private readonly LogHandler _logHandler;
        private readonly ExportHandler _exportHandler;
        private readonly DataProcessor _dataProcessor;
        private readonly ReportGenerator _reportGenerator;

        // State variables
        private readonly List<string> ScrapedUrls;
        private readonly List<string> LogMessages;
        private CancellationTokenSource _cancellationTokenSource;
        private bool stop_isclicked = false;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize handlers and utilities
            _databaseHandler = new DatabaseHandler();
            _logHandler = new LogHandler(logRichTextBox, "ScrapeLogs.txt"); // Initialize with log file path
            _scraper = new Scraper(_logHandler);
            _urlDetailsFetcher = new UrlDetailsFetcher();
            _exportHandler = new ExportHandler();
            _reportGenerator = new ReportGenerator();
            ScrapedUrls = new List<string>();
            LogMessages = new List<string>();

            _databaseHandler.InitializeDatabase();
            _dataProcessor = new DataProcessor();
        }

        // Button click to start scraping process
        private async void startScrapingButton_Click(object sender, RoutedEventArgs e)
        {
            // Fetch details of the entered URL
            string url = urlTextBox.Text.Trim();
            var urlDetails = await _urlDetailsFetcher.FetchUrlDetails(url, (msg, level) => _logHandler.UpdateLogAsync(msg, level));
            DisplayUrlDetails(urlDetails);

            // Validate URL
            if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                MessageBox.Show("Please enter a valid URL.", "netDigger");
                return;
            }

            // Open dialog to select data types and path
            var selectDataTypesWindow = new SelectDataTypesWindow();
            bool? dialogResult = selectDataTypesWindow.ShowDialog();

            // Check if dialog was canceled
            if (dialogResult != true)
                return;

            // Retrieve selected data types and path
            List<string> selectedDataTypes = selectDataTypesWindow.SelectedDataTypes;
            string selectedPath = selectDataTypesWindow.SelectedPath;

            // Initialize data processor with selected options
            _dataProcessor.Initialize(selectedDataTypes, selectedPath);

            // Prepare UI for scraping process
            startScrapingButton.Visibility = Visibility.Hidden;
            stopScrapingButton.Visibility = Visibility.Visible;
            exportDatabaseCheckBox.Visibility = Visibility.Hidden;
            exportTxtCheckBox.Visibility = Visibility.Hidden;
            exportCsvCheckBox.Visibility = Visibility.Hidden;
            logRichTextBox.Document.Blocks.Clear();
            ScrapedUrls.Clear();
            LogMessages.Clear();
            statusTextBlock.Text = "Scraping in progress...";
            _cancellationTokenSource = new CancellationTokenSource();

            // Start scraping asynchronously
            bool success = await _scraper.ScrapeAsync(url, ScrapedUrls, _cancellationTokenSource.Token);

            if (success)
            {
                // Update UI after successful scraping
                startScrapingButton.Visibility = Visibility.Visible;
                stopScrapingButton.Visibility = Visibility.Hidden;
                exportDatabaseCheckBox.Visibility = Visibility.Visible;
                exportTxtCheckBox.Visibility = Visibility.Visible;
                exportCsvCheckBox.Visibility = Visibility.Visible;

                // Organize scraped URLs
                await _dataProcessor.OrganizeUrlsAsync(ScrapedUrls);
                MessageBox.Show("Data organization into databases completed successfully.", "netDigger");
                await _logHandler.UpdateLogAsync("Scraping completed successfully.");
                await HandleExport();
                statusTextBlock.Text = "Ready";
            }
            else
            {
                // Handle scraping failure
                if (stop_isclicked)
                {
                    stopScrapingButton.Visibility = Visibility.Hidden;
                    startScrapingButton.Visibility = Visibility.Visible;
                    exportDatabaseCheckBox.Visibility = Visibility.Visible;
                    exportTxtCheckBox.Visibility = Visibility.Visible;
                    exportCsvCheckBox.Visibility = Visibility.Visible;
                    await _logHandler.UpdateLogAsync("Scraping process has been terminated by the user.");
                    statusTextBlock.Text = "Ready";
                }
                else
                {
                    stopScrapingButton.Visibility = Visibility.Hidden;
                    startScrapingButton.Visibility = Visibility.Visible;
                    exportDatabaseCheckBox.Visibility = Visibility.Visible;
                    exportTxtCheckBox.Visibility = Visibility.Visible;
                    exportCsvCheckBox.Visibility = Visibility.Visible;
                    await _logHandler.UpdateLogAsync("Scraping failed due to errors.", "ERROR");
                    statusTextBlock.Text = "Ready";
                }
            }

            // Generate and export reports after scraping is done
            await GenerateAndExportReports();
        }

        // Button click to stop scraping process
        private async void stopScrapingButton_Click(object sender, RoutedEventArgs e)
        {
            stop_isclicked = true;
            _cancellationTokenSource.Cancel();

            // Organize scraped URLs
            await _dataProcessor.OrganizeUrlsAsync(ScrapedUrls);
            await _logHandler.UpdateLogAsync("Data structuring into appropriate .DB completed.", "INFO");
            MessageBox.Show("Data organization into databases completed successfully.", "netDigger");
            Dispatcher.Invoke(async () => await HandleExport());
        }

        // Handle exporting URLs to various formats
        private async Task HandleExport()
        {
            if (exportDatabaseCheckBox.IsChecked == true)
            {
                _databaseHandler.SaveUrlsToDatabase(ScrapedUrls);
                await _logHandler.UpdateLogAsync("URLs exported to database successfully.", messageColor: Colors.Cyan);
            }
            if (exportCsvCheckBox.IsChecked == true)
            {
                await _exportHandler.SaveUrlsToCsvAsync(ScrapedUrls);
                await _logHandler.UpdateLogAsync("URLs exported to CSV successfully.", messageColor: Colors.Cyan);
            }
            if (exportTxtCheckBox.IsChecked == true)
            {
                await _exportHandler.SaveUrlsToTxtAsync(ScrapedUrls);
                await _logHandler.UpdateLogAsync("URLs exported to TXT successfully.", messageColor: Colors.Cyan);
            }
        }

        // Generate and export reports after scraping process
        private async Task GenerateAndExportReports()
        {
            var summaryReport = _reportGenerator.GenerateSummaryReport(ScrapedUrls);

            _reportGenerator.ExportReportToFile(summaryReport, "SummaryReport.txt");

            await _logHandler.UpdateLogAsync("Reports generated and exported successfully.", messageColor: Colors.Green);
        }

        // Display URL details in the UI
        private void DisplayUrlDetails(string details)
        {
            if (details != null)
            {
                Dispatcher.Invoke(() =>
                {
                    urlDetailsTextBlock.Text = details;
                });
            }
        }

        // Minimize the window
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // Close the application
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Handle dragging the window by clicking on the title bar
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
