using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using NetDigger.Core;
using NetDigger.Core.Extraction;
using NetDigger.Core.Reporting;
using NetDigger.Core.Storage;

namespace NetDigger.App;

public enum SpeedProfile { Gentle, Normal, Fast }
public enum ResultFilter { All, Pages, Files, Broken }
public enum ResultsTab { Results, Log }

/// <summary>A toggleable group of resource kinds ("Documents", "Images", ...).</summary>
public sealed class KindChip(string label, params ResourceKind[] kinds) : ObservableObject
{
    private bool _isOn = true;

    public string Label { get; } = label;
    public IReadOnlyList<ResourceKind> Kinds { get; } = kinds;

    public bool IsOn
    {
        get => _isOn;
        set => SetProperty(ref _isOn, value);
    }
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaxRows = 20_000;
    private static readonly int[] PagePresets = { 50, 100, 250, 500, 1000, 2500, 5000, 10000, 50000 };

    private readonly ConcurrentQueue<ResourceRecord> _feed = new();
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;

    private string _url = string.Empty;
    private bool _isRunning, _hasRun, _isOptionsOpen, _statusIsError;
    private CrawlScope _scope = CrawlScope.SameHost;
    private int _depth = 3, _pageIndex = 3;
    private SpeedProfile _speed = SpeedProfile.Normal;
    private bool _respectRobots = true, _useSitemaps = true, _probeAssets = true, _checkExternal = true, _mineScripts = true;
    private string _includePattern = string.Empty, _excludePattern = string.Empty;
    private string _outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "netDigger");
    private int _discovered, _fetched, _broken, _queued;
    private double _fraction;
    private string _elapsed = "00:00", _currentUrl = string.Empty, _statusText = "Ready";
    private string _searchText = string.Empty;
    private ResultFilter _filter = ResultFilter.All;
    private ResultsTab _tab = ResultsTab.Results;
    private string? _outputDir;
    private string _rowCountText = string.Empty;

    public MainViewModel()
    {
        Chips = new[]
        {
            new KindChip("Documents", ResourceKind.Pdf, ResourceKind.Document, ResourceKind.Spreadsheet,
                ResourceKind.Presentation, ResourceKind.Text, ResourceKind.Csv),
            new KindChip("Images", ResourceKind.Image),
            new KindChip("Media", ResourceKind.Video, ResourceKind.Audio),
            new KindChip("Data", ResourceKind.Json, ResourceKind.Xml, ResourceKind.Database),
            new KindChip("Archives", ResourceKind.Archive),
            new KindChip("Code", ResourceKind.Script, ResourceKind.Stylesheet, ResourceKind.Font)
        };

        ResultsView = CollectionViewSource.GetDefaultView(Rows);
        ResultsView.Filter = FilterRow;

        StartStopCommand = new RelayCommand(StartOrStop);
        ToggleOptionsCommand = new RelayCommand(() => IsOptionsOpen = !IsOptionsOpen);
        DepthDownCommand = new RelayCommand(() => Depth = Math.Max(1, Depth - 1));
        DepthUpCommand = new RelayCommand(() => Depth = Math.Min(10, Depth + 1));
        PagesDownCommand = new RelayCommand(() => PageIndex = Math.Max(0, PageIndex - 1));
        PagesUpCommand = new RelayCommand(() => PageIndex = Math.Min(PagePresets.Length - 1, PageIndex + 1));
        BrowseCommand = new RelayCommand(Browse);
        OpenReportCommand = new RelayCommand(() => OpenPath("report.html"), () => CanOpenOutput);
        OpenCsvCommand = new RelayCommand(() => OpenPath("resources.csv"), () => CanOpenOutput);
        OpenFolderCommand = new RelayCommand(() => OpenPath(string.Empty), () => CanOpenOutput);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    // ---- collections -----------------------------------------------------------------------------------------

    public ObservableCollection<ResourceRow> Rows { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ICollectionView ResultsView { get; }
    public IReadOnlyList<KindChip> Chips { get; }

    // ---- commands --------------------------------------------------------------------------------------------

    public ICommand StartStopCommand { get; }
    public ICommand ToggleOptionsCommand { get; }
    public ICommand DepthDownCommand { get; }
    public ICommand DepthUpCommand { get; }
    public ICommand PagesDownCommand { get; }
    public ICommand PagesUpCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand OpenCsvCommand { get; }
    public ICommand OpenFolderCommand { get; }

    // ---- input -----------------------------------------------------------------------------------------------

    public string Url { get => _url; set => SetProperty(ref _url, value); }
    public CrawlScope Scope { get => _scope; set => SetProperty(ref _scope, value); }
    public SpeedProfile Speed { get => _speed; set => SetProperty(ref _speed, value); }
    public bool RespectRobots { get => _respectRobots; set => SetProperty(ref _respectRobots, value); }
    public bool UseSitemaps { get => _useSitemaps; set => SetProperty(ref _useSitemaps, value); }
    public bool ProbeAssets { get => _probeAssets; set => SetProperty(ref _probeAssets, value); }
    public bool CheckExternal { get => _checkExternal; set => SetProperty(ref _checkExternal, value); }
    public bool MineScripts { get => _mineScripts; set => SetProperty(ref _mineScripts, value); }
    public string IncludePattern { get => _includePattern; set => SetProperty(ref _includePattern, value); }
    public string ExcludePattern { get => _excludePattern; set => SetProperty(ref _excludePattern, value); }
    public string OutputRoot { get => _outputRoot; set => SetProperty(ref _outputRoot, value); }

    public int Depth { get => _depth; set => SetProperty(ref _depth, value); }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (SetProperty(ref _pageIndex, value)) OnPropertyChanged(nameof(MaxPages));
        }
    }

    public int MaxPages => PagePresets[PageIndex];

    public bool IsOptionsOpen { get => _isOptionsOpen; set => SetProperty(ref _isOptionsOpen, value); }

    // ---- run state -------------------------------------------------------------------------------------------

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(StartGlyph));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    public bool IsIdle => !IsRunning;
    public string StartLabel => IsRunning ? "Stop" : "Start";
    public string StartGlyph => IsRunning ? "\uE71A" : "\uE768";
    public bool HasRun
    {
        get => _hasRun;
        private set
        {
            if (SetProperty(ref _hasRun, value)) OnPropertyChanged(nameof(EmptyHint));
        }
    }

    public int Discovered { get => _discovered; private set => SetProperty(ref _discovered, value); }
    public int Fetched { get => _fetched; private set => SetProperty(ref _fetched, value); }
    public int Queued { get => _queued; private set => SetProperty(ref _queued, value); }

    public int Broken
    {
        get => _broken;
        private set
        {
            if (SetProperty(ref _broken, value)) OnPropertyChanged(nameof(HasBroken));
        }
    }

    public bool HasBroken => Broken > 0;
    public double Fraction { get => _fraction; private set => SetProperty(ref _fraction, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string CurrentUrl { get => _currentUrl; private set => SetProperty(ref _currentUrl, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public string RowCountText { get => _rowCountText; private set => SetProperty(ref _rowCountText, value); }

    public bool CanOpenOutput => _outputDir is not null && !IsRunning;

    // ---- results view ----------------------------------------------------------------------------------------

    public ResultsTab Tab { get => _tab; set => SetProperty(ref _tab, value); }

    public ResultFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) ResultsView.Refresh();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ResultsView.Refresh();
        }
    }

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyHint => IsRunning
        ? "Waiting for the first response..."
        : HasRun ? "Nothing was found." : "Results appear here as they are found.";

    private bool FilterRow(object item)
    {
        var row = (ResourceRow)item;
        var matches = Filter switch
        {
            ResultFilter.Pages => row.IsPage,
            ResultFilter.Files => !row.IsPage,
            ResultFilter.Broken => row.IsBroken,
            _ => true
        };

        return matches && (SearchText.Length == 0 || row.Url.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    // ---- crawling --------------------------------------------------------------------------------------------

    private void StartOrStop()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            StatusText = "Stopping and saving what was found...";
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        if (!UrlNormalizer.TryParseSeed(Url, out var seed))
        {
            SetStatus("Enter a valid web address, for example example.com.", isError: true);
            return;
        }

        CrawlOptions options;
        try
        {
            options = BuildOptions(seed);
        }
        catch (ArgumentException ex)
        {
            SetStatus($"Invalid pattern: {ex.Message}", isError: true);
            return;
        }

        var outputDir = Path.Combine(OutputRoot, $"{seed.Host}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var dbPath = Path.Combine(outputDir, "crawl.db");

        ResetRun();
        IsRunning = true;
        HasRun = true;
        Tab = ResultsTab.Results;
        SetStatus("Crawling...", isError: false);

        _cts = new CancellationTokenSource();
        CrawlSummary? summary = null;
        string? error = null;

        try
        {
            await using var store = SqliteCrawlStore.Create(dbPath);
            var sink = new LiveSink(store, _feed);
            using var engine = new CrawlEngine(options, sink, new UiLogger(_logQueue));
            summary = await engine.RunAsync(new Progress<CrawlProgress>(ApplyProgress), _cts.Token);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (summary is not null)
        {
            SetStatus("Writing report...", isError: false);
            try
            {
                await Task.Run(() =>
                {
                    CrawlExporter.ExportReportHtml(dbPath, Path.Combine(outputDir, "report.html"));
                    CrawlExporter.ExportResourcesCsv(dbPath, Path.Combine(outputDir, "resources.csv"));
                    CrawlExporter.ExportUrlsByKind(dbPath, Path.Combine(outputDir, "urls"));
                });
                _outputDir = outputDir;
            }
            catch (Exception ex)
            {
                error = $"Crawl finished but the report failed: {ex.Message}";
            }
        }

        _cts.Dispose();
        _cts = null;
        Drain();
        IsRunning = false;
        OnPropertyChanged(nameof(CanOpenOutput));
        CommandManager.InvalidateRequerySuggested();

        if (error is not null) SetStatus(error, isError: true);
        else if (summary is not null) SetStatus(Describe(summary), isError: false);
    }

    private static string Describe(CrawlSummary summary)
    {
        var f = summary.Final;
        var lead = summary.Status switch
        {
            CrawlStatus.Cancelled => "Stopped",
            CrawlStatus.PageLimitReached => "Page limit reached",
            _ => "Done"
        };
        return $"{lead} - {f.Discovered:N0} URLs, {f.Broken:N0} broken";
    }

    private CrawlOptions BuildOptions(Uri seed)
    {
        var (delayMs, workers) = Speed switch
        {
            SpeedProfile.Gentle => (400, 4),
            SpeedProfile.Fast => (30, 16),
            _ => (150, 8)
        };

        HashSet<ResourceKind>? kinds = null;
        if (Chips.Any(c => !c.IsOn))
        {
            kinds = new HashSet<ResourceKind> { ResourceKind.Other };
            foreach (var chip in Chips.Where(c => c.IsOn))
                foreach (var kind in chip.Kinds) kinds.Add(kind);
        }

        return new CrawlOptions
        {
            Seeds = new[] { seed },
            MaxDepth = Depth,
            MaxPages = MaxPages,
            Concurrency = workers,
            PerHostDelay = TimeSpan.FromMilliseconds(delayMs),
            Scope = Scope,
            RespectRobotsTxt = RespectRobots,
            UseSitemaps = UseSitemaps,
            ProbeResources = ProbeAssets,
            CheckExternalLinks = CheckExternal,
            AnalyzeScripts = MineScripts,
            Include = ParsePattern(IncludePattern),
            Exclude = ParsePattern(ExcludePattern),
            CollectKinds = kinds
        };
    }

    private static IReadOnlyList<Regex> ParsePattern(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<Regex>()
            : new[]
            {
                new Regex(text.Trim(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
            };

    private void ResetRun()
    {
        while (_feed.TryDequeue(out _)) { }
        while (_logQueue.TryDequeue(out _)) { }
        Rows.Clear();
        LogLines.Clear();
        _outputDir = null;
        Discovered = Fetched = Broken = Queued = 0;
        Fraction = 0;
        Elapsed = "00:00";
        CurrentUrl = string.Empty;
        RowCountText = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanOpenOutput));
    }

    private void ApplyProgress(CrawlProgress p)
    {
        Discovered = p.Discovered;
        Fetched = p.Fetched + p.Probed;
        Broken = p.Broken + p.Failed;
        Queued = p.Queued;
        Fraction = p.Discovered == 0 ? 0 : Math.Clamp((double)(p.Discovered - p.Queued) / p.Discovered, 0, 1);
        Elapsed = p.Elapsed.TotalHours >= 1 ? p.Elapsed.ToString(@"h\:mm\:ss") : p.Elapsed.ToString(@"mm\:ss");
        CurrentUrl = p.CurrentUrl ?? string.Empty;
    }

    /// <summary>Moves queued rows and log lines into the UI collections in small batches.</summary>
    private void Drain()
    {
        var added = 0;
        while (added < 500 && _feed.TryDequeue(out var record))
        {
            if (Rows.Count >= MaxRows) continue;
            Rows.Add(new ResourceRow(record));
            added++;
        }

        if (added > 0)
        {
            OnPropertyChanged(nameof(IsEmpty));
            RowCountText = Rows.Count >= MaxRows
                ? $"first {MaxRows:N0} shown, the rest are in the report"
                : $"{Rows.Count:N0} URLs";
        }

        var logged = false;
        while (_logQueue.TryDequeue(out var line))
        {
            LogLines.Insert(0, line);
            logged = true;
        }

        if (logged)
            while (LogLines.Count > 1000) LogLines.RemoveAt(LogLines.Count - 1);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        StatusIsError = isError;
    }

    // ---- files -----------------------------------------------------------------------------------------------

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where crawls are saved" };
        if (dialog.ShowDialog() == true) OutputRoot = dialog.FolderName;
    }

    private void OpenPath(string relative)
    {
        if (_outputDir is null) return;

        var path = relative.Length == 0 ? _outputDir : Path.Combine(_outputDir, relative);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenUrl(ResourceRow row)
    {
        if (Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _timer.Stop();
    }
}
