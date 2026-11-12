using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace netDigger
{
    public class LogHandler : IDisposable
    {
        private readonly RichTextBox _logRichTextBox;
        private readonly ConcurrentQueue<LogMessage> _logMessages = new ConcurrentQueue<LogMessage>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly Timer _logProcessingTimer;
        private readonly StreamWriter _logWriter;

        // Constructor: Initializes LogHandler with a RichTextBox for logging and a log file path
        public LogHandler(RichTextBox logRichTextBox, string logFilePath)
        {
            _logRichTextBox = logRichTextBox;
            _logWriter = new StreamWriter(logFilePath, append: true) { AutoFlush = true }; // Initialize StreamWriter for log file
            _logProcessingTimer = new Timer(ProcessLogQueue, null, 100, 100); // Initialize timer for processing log messages
        }

        // UpdateLogAsync method: Enqueues a log message for asynchronous processing and writes it to the log file
        public Task UpdateLogAsync(string message, string level = "INFO", int threadId = -1, string methodName = "", Color? messageColor = null)
        {
            var fullMessage = !string.IsNullOrEmpty(methodName) ? $"{methodName}: {message}" : message;
            var logMessage = new LogMessage { Message = fullMessage, Level = level, ThreadId = threadId, MessageColor = messageColor ?? Colors.Black };

            _logMessages.Enqueue(logMessage);
            WriteLogToFile(logMessage); // Write log message to file

            return Task.CompletedTask;
        }

        // WriteLogToFile method: Writes a log message to the log file
        private void WriteLogToFile(LogMessage logMessage)
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss}] [{logMessage.Level}]";

            if (logMessage.ThreadId >= 0)
                logEntry += $" [Thread {logMessage.ThreadId}]";

            logEntry += $" {logMessage.Message}";

            _logWriter.WriteLine(logEntry);
        }

        // ProcessLogQueue method: Processes queued log messages on the UI thread
        private void ProcessLogQueue(object state)
        {
            if (_logMessages.IsEmpty) return;

            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                while (_logMessages.TryDequeue(out var logMessage))
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    var timeRun = new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Brushes.Gray };
                    var levelRun = new Run($"[{logMessage.Level}] ") { Foreground = GetLogLevelColor(logMessage.Level) };
                    var messageRun = new Run(logMessage.Message) { Foreground = new SolidColorBrush(logMessage.MessageColor) };

                    paragraph.Inlines.Add(timeRun);
                    paragraph.Inlines.Add(levelRun);

                    if (logMessage.ThreadId >= 0)
                    {
                        var threadRun = new Run($"[Thread {logMessage.ThreadId}] ") { Foreground = Brushes.Purple };
                        paragraph.Inlines.Add(threadRun);
                    }

                    paragraph.Inlines.Add(messageRun);
                    _logRichTextBox.Document.Blocks.Add(paragraph);
                }
                _logRichTextBox.ScrollToEnd();
            }));
        }

        // GetLogLevelColor method: Returns a SolidColorBrush based on log level
        private SolidColorBrush GetLogLevelColor(string level)
        {
            return level switch
            {
                "INFO" => Brushes.Green,
                "WARNING" => Brushes.Orange,
                "ERROR" => Brushes.Red,
                _ => Brushes.Black,
            };
        }

        // Dispose method: Cleans up resources when LogHandler is disposed
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _logProcessingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _logProcessingTimer.Dispose();
            while (_logMessages.TryDequeue(out _)) { }
            _logWriter?.Dispose();
        }

        // LogMessage class: Represents a log message with details
        private class LogMessage
        {
            public string Message { get; set; }
            public string Level { get; set; }
            public int ThreadId { get; set; }
            public Color MessageColor { get; set; }
        }
    }
}