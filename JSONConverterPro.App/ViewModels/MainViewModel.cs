using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSONConverterPro.Core.Models;
using JSONConverterPro.Core.Services;
using JSONConverterPro.Models;
using JSONConverterPro.Services;
using Microsoft.Extensions.Logging;

namespace JSONConverterPro.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly JsonSchemaService _schema;
    private readonly ConversionService _conv;
    private readonly PreviewService _preview;
    private readonly FileService _files;
    private readonly IDialogService _dialogs;
    private readonly AppStateService _state;
    private readonly ILogger<MainViewModel> _log;
    private CancellationTokenSource? _previewCts, _convertCts, _debounceCts;
    private bool _loading = true;

    public MainViewModel(JsonSchemaService schema, ConversionService conv, PreviewService preview, FileService files,
        IDialogService dialogs, AppStateService state, LogStore logStore, ILogger<MainViewModel> log)
    {
        _schema = schema; _conv = conv; _preview = preview; _files = files; _dialogs = dialogs; _state = state; _log = log;
        LogEntries = logStore.Entries;
        foreach (var h in state.State.History.Take(100)) History.Add(h);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _outputDirectory = state.State.LastOutputDirectory is { Length: > 0 } d ? d : Path.Combine(docs, "JSON Converter Pro");
        Version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        _loading = false;
    }

    // ---------------- State

    public string Version { get; }
    public ObservableCollection<FileItem> Files { get; } = new();
    public ObservableCollection<DataSourceCandidate> Candidates { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; }
    public string LogDirectory => FileLoggerProvider.DefaultDirectory;

    public event Action? PreviewChanged;
    public IReadOnlyList<string> PreviewColumns { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string[]> PreviewRows { get; private set; } = Array.Empty<string[]>();

    [ObservableProperty] private string _currentPage = "Files";
    [ObservableProperty] private FileItem? _selectedFile;
    [ObservableProperty] private DataSourceCandidate? _selectedCandidate;

    // Settings
    [ObservableProperty] private bool _isExcel = true;
    [ObservableProperty] private string _sheetName = "Data";
    [ObservableProperty] private bool _flattenNested = true;
    [ObservableProperty] private bool _parseDates = true;
    [ObservableProperty] private int _arrayModeIndex;
    [ObservableProperty] private int _orderIndex;
    [ObservableProperty] private int _nullIndex;
    [ObservableProperty] private int _delimiterIndex;
    [ObservableProperty] private string _customOrderText = "";
    [ObservableProperty] private string _outputDirectory;

    // Progress and feedback
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerKind = "info";
    [ObservableProperty] private bool _bannerHasFolder;

    // Preview
    [ObservableProperty] private bool _previewBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewTotalsText = "";
    [ObservableProperty] private string _previewMessage = "";
    [ObservableProperty] private string _previewWarnings = "";
    [ObservableProperty] private int _previewColumnCount;
    [ObservableProperty] private long _previewRecordCount;

    public bool HasFiles => Files.Count > 0;
    public bool IsCsv { get => !IsExcel; set => IsExcel = !value; }
    public bool ShowCandidates => Candidates.Count > 1;
    public bool IsFilesPage => CurrentPage == "Files";
    public bool IsPreviewPage => CurrentPage == "Preview";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsHistoryPage => CurrentPage == "History";
    public bool IsLogPage => CurrentPage == "Log";
    public bool IsCustomOrder => OrderIndex == 3;
    public bool ShowDelimiter => !IsExcel;
    public bool ShowSheetName => IsExcel;
    public string FilesCountText => Files.Count.ToString("N0");
    public string RecordsTotalText => Files.Sum(f => f.SelectedSource?.Count ?? 0).ToString("N0");
    public string ColumnsText => HasPreview ? PreviewColumnCount.ToString("N0") : "–";
    public bool BannerIsError => BannerKind == "error";
    public bool BannerIsSuccess => BannerKind == "success";
    public bool BannerIsWarning => BannerKind is "warning" or "info";
    public string ArrayHint => ArrayModeIndex switch
    {
        0 => "Arrays become one cell separated by semicolons. Arrays of objects keep one column per field, so nothing is lost.",
        1 => "Each array is kept as compact JSON text in a single cell.",
        2 => "Each array element becomes its own row; parent values repeat. Very large combinations fall back to joining.",
        _ => "Arrays of objects are written to extra worksheets (or extra CSV files), linked by _recordNo."
    };

    // ---------------- Settings

    private ConversionSettings ToSettings() => new()
    {
        OutputFormat = IsExcel ? OutputFormat.Excel : OutputFormat.Csv,
        SheetName = string.IsNullOrWhiteSpace(SheetName) ? "Data" : SheetName.Trim(),
        FlattenNestedObjects = FlattenNested,
        ParseDates = ParseDates,
        ArrayHandling = (ArrayHandling)Math.Clamp(ArrayModeIndex, 0, 3),
        ColumnOrdering = (ColumnOrdering)Math.Clamp(OrderIndex, 0, 3),
        NullHandling = (NullHandling)Math.Clamp(NullIndex, 0, 1),
        CsvDelimiter = (CsvDelimiter)Math.Clamp(DelimiterIndex, 0, 2),
        CustomColumnOrder = CustomOrderText.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
    };

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsFilesPage)); OnPropertyChanged(nameof(IsPreviewPage)); OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsHistoryPage)); OnPropertyChanged(nameof(IsLogPage));
    }
    partial void OnIsExcelChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCsv)); OnPropertyChanged(nameof(ShowDelimiter)); OnPropertyChanged(nameof(ShowSheetName));
    }
    partial void OnArrayModeIndexChanged(int value) { OnPropertyChanged(nameof(ArrayHint)); SchedulePreview(); }
    partial void OnOrderIndexChanged(int value) { OnPropertyChanged(nameof(IsCustomOrder)); SchedulePreview(); }
    partial void OnNullIndexChanged(int value) => SchedulePreview();
    partial void OnFlattenNestedChanged(bool value) => SchedulePreview();
    partial void OnParseDatesChanged(bool value) => SchedulePreview();
    partial void OnCustomOrderTextChanged(string value) { if (IsCustomOrder) SchedulePreview(); }
    partial void OnOutputDirectoryChanged(string value)
    {
        if (_loading) return;
        _state.State.LastOutputDirectory = value; _state.Save();
    }
    partial void OnBannerKindChanged(string value)
    {
        OnPropertyChanged(nameof(BannerIsError)); OnPropertyChanged(nameof(BannerIsSuccess)); OnPropertyChanged(nameof(BannerIsWarning));
    }
    partial void OnPreviewColumnCountChanged(int value) => OnPropertyChanged(nameof(ColumnsText));
    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(ColumnsText));

    // ---------------- Banner

    public void ShowError(string text) => Banner(text, "error", false);
    private void Banner(string text, string kind, bool folder)
    {
        BannerText = text; BannerKind = kind; BannerHasFolder = folder; ShowBanner = true;
    }

    [RelayCommand] private void DismissBanner() => ShowBanner = false;
    [RelayCommand] private void Navigate(string page) => CurrentPage = page;

    // ---------------- Files

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var paths = await _dialogs.PickFilesAsync();
        await AddPathsAsync(paths);
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<FileItem>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Files.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new FileItem(p);
            if (!_files.IsSupported(p))
            {
                item.Status = FileStatus.Invalid; item.StatusText = "Unsupported";
                item.Detail = "This file type is not supported. Choose a .json or .txt file.";
                Banner($"'{item.Name}' isn't supported. Choose .json or .txt files.", "warning", false);
            }
            Files.Add(item);
            added.Add(item);
        }
        RaiseFileStats();
        if (SelectedFile == null)
        {
            var first = added.FirstOrDefault(a => a.Status == FileStatus.Analyzing) ?? added.FirstOrDefault();
            if (first != null) SelectFile(first);
        }
        foreach (var item in added.Where(a => a.Status == FileStatus.Analyzing))
            await AnalyzeAsync(item);
    }

    private async Task AnalyzeAsync(FileItem item)
    {
        try
        {
            var info = await Task.Run(() =>
            {
                _files.ValidateInput(item.Path);
                using var s = _files.OpenRead(item.Path);
                return _schema.Analyze(s, CancellationToken.None);
            });
            if (!Files.Contains(item)) return;   // removed meanwhile
            if (info.IsEmpty) Fail(item, "The file contains no JSON data.");
            else if (info.IsUnsupportedRoot) Fail(item, "The JSON must be an object or an array to convert it.");
            else
            {
                item.Structure = info;
                if (info.RequiresSelection)
                {
                    item.Status = FileStatus.NeedsSelection; item.StatusText = "Choose data array";
                    item.SelectedSource = null; item.RecordsText = "Several arrays found";
                    Banner($"'{item.Name}' has several data arrays. Open Preview and choose the one to export.", "warning", false);
                }
                else
                {
                    item.Status = FileStatus.Ready; item.StatusText = "Ready";
                    item.SelectedSource = info.Suggested;
                    item.RecordsText = $"{info.Suggested!.Count:N0} records";
                }
            }
        }
        catch (Exception ex)
        {
            if (!Files.Contains(item)) return;
            Fail(item, _files.Describe(ex));
            _log.LogWarning(ex, "Could not read {File}", item.Path);
        }
        RaiseFileStats();
        if (SelectedFile == item) { RefreshCandidates(); await RefreshPreviewAsync(); }
    }

    private static void Fail(FileItem item, string message)
    {
        item.Status = FileStatus.Invalid; item.StatusText = "Invalid"; item.Detail = message; item.RecordsText = "";
    }

    public void SelectFile(FileItem? item)
    {
        if (ReferenceEquals(item, SelectedFile)) return;
        foreach (var f in Files) f.IsSelected = ReferenceEquals(f, item);
        SelectedFile = item;
        ClearPreview();                       // never show records from the previous file
        RefreshCandidates();
        _ = RefreshPreviewAsync();
    }

    private void RefreshCandidates()
    {
        Candidates.Clear();
        var s = SelectedFile?.Structure;
        if (s != null) foreach (var c in s.Candidates) Candidates.Add(c);
        _suppressCandidate = true;
        SelectedCandidate = SelectedFile?.SelectedSource;
        _suppressCandidate = false;
        OnPropertyChanged(nameof(ShowCandidates));
    }

    private bool _suppressCandidate;
    partial void OnSelectedCandidateChanged(DataSourceCandidate? value)
    {
        if (_suppressCandidate || value == null || SelectedFile == null) return;
        var f = SelectedFile;
        f.SelectedSource = value;
        f.RecordsText = $"{value.Count:N0} records";
        if (f.Status == FileStatus.NeedsSelection) { f.Status = FileStatus.Ready; f.StatusText = "Ready"; }
        ShowBanner = false;
        RaiseFileStats();
        _ = RefreshPreviewAsync();
    }

    public void RemoveFile(FileItem item)
    {
        if (IsBusy) return;
        bool wasSelected = ReferenceEquals(item, SelectedFile);
        Files.Remove(item);
        if (wasSelected)
        {
            SelectedFile = null;
            ClearPreview();
            RefreshCandidates();
            var next = Files.FirstOrDefault();
            if (next != null) SelectFile(next);
        }
        RaiseFileStats();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        if (IsBusy) return;
        _previewCts?.Cancel();
        Files.Clear();
        SelectedFile = null; ClearPreview(); RefreshCandidates(); RaiseFileStats(); ShowBanner = false;
    }

    private void RaiseFileStats()
    {
        OnPropertyChanged(nameof(HasFiles)); OnPropertyChanged(nameof(FilesCountText)); OnPropertyChanged(nameof(RecordsTotalText));
    }

    // ---------------- Preview

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewColumns = Array.Empty<string>(); PreviewRows = Array.Empty<string[]>();
        HasPreview = false; PreviewTotalsText = ""; PreviewMessage = ""; PreviewWarnings = ""; PreviewColumnCount = 0; PreviewRecordCount = 0;
        PreviewChanged?.Invoke();
    }

    private void SchedulePreview()
    {
        if (_loading) return;
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(() => _ = RefreshPreviewAsync());
        });
    }

    [RelayCommand]
    public async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        var file = SelectedFile;
        if (file == null || file.SelectedSource == null)
        {
            ClearPreview();
            PreviewMessage = file?.Status == FileStatus.NeedsSelection ? "Choose a data array above to see the preview." : file?.Detail ?? "";
            return;
        }
        var cts = _previewCts = new CancellationTokenSource();
        var ct = cts.Token;
        var settings = ToSettings();
        var src = file.SelectedSource;
        PreviewBusy = true; PreviewMessage = "";
        try
        {
            var result = await Task.Run(() => _preview.Build(file.Path, src, settings, 10, ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(file, SelectedFile)) return;
            PreviewColumns = result.ColumnNames; PreviewRows = result.Rows;
            PreviewRecordCount = result.TotalRecords; PreviewColumnCount = result.TotalColumns;
            HasPreview = true;
            PreviewTotalsText = $"{result.TotalRecords:N0} records · {result.TotalColumns:N0} columns" +
                                (result.ChildTables.Count > 0 ? $" · {result.ChildTables.Count} linked sheet(s): {string.Join(", ", result.ChildTables)}" : "");
            PreviewWarnings = string.Join(Environment.NewLine, result.Warnings.Take(5));
            file.RecordsText = $"{result.TotalRecords:N0} records";
            PreviewChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(file, SelectedFile)) return;
            ClearPreview();
            string msg = _files.Describe(ex);
            PreviewMessage = msg;
            file.Status = FileStatus.Invalid; file.StatusText = "Invalid"; file.Detail = msg;
            _log.LogWarning(ex, "Preview failed for {File}", file.Path);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts)) PreviewBusy = false;
        }
    }

    // ---------------- Output folder

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var f = await _dialogs.PickFolderAsync();
        if (!string.IsNullOrEmpty(f)) OutputDirectory = f;
    }

    [RelayCommand] private void OpenOutputFolder() => ShellService.OpenFolder(OutputDirectory);
    [RelayCommand] private void OpenLogFolder() => ShellService.OpenFolder(LogDirectory);
    [RelayCommand] private void ClearLog() => LogEntries.Clear();
    [RelayCommand] private void ClearHistory() { History.Clear(); _state.State.History.Clear(); _state.Save(); }

    // ---------------- Conversion

    [RelayCommand]
    private void Cancel()
    {
        _convertCts?.Cancel();
        StatusText = "Cancelling…";
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsBusy) return;
        var targets = Files.Where(f => f.Status is FileStatus.Ready or FileStatus.Done or FileStatus.Failed or FileStatus.Cancelled && f.SelectedSource != null).ToList();
        if (targets.Count == 0)
        {
            var pending = Files.FirstOrDefault(f => f.Status == FileStatus.NeedsSelection);
            if (pending != null) Banner($"Choose the data array for '{pending.Name}' in Preview first.", "warning", false);
            else Banner(Files.Count == 0 ? "Add at least one JSON file to convert." : "None of the added files can be converted. Remove them and add a valid file.", "warning", false);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputDirectory)) { Banner("Choose an output folder first.", "warning", false); return; }

        IsBusy = true; ShowBanner = false; ProgressPercent = 0;
        var cts = _convertCts = new CancellationTokenSource();
        var settings = ToSettings();
        string outDir = OutputDirectory;
        int ok = 0, failed = 0, cancelled = 0;
        var lastMessage = "";
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var file = targets[i];
                int index = i;
                file.Status = FileStatus.Converting; file.StatusText = "Converting…"; file.Detail = "";
                StatusText = targets.Count > 1 ? $"Converting {file.Name} ({i + 1} of {targets.Count})" : $"Converting {file.Name}";
                var progress = new Progress<ConversionProgress>(p =>
                {
                    ProgressPercent = (index + p.Fraction) / targets.Count * 100;
                    ProgressText = p.Records > 0 ? $"{p.Phase} {p.Records:N0} records" : p.Phase;
                });

                string baseName = Path.GetFileNameWithoutExtension(file.Path);
                string unique = baseName; int n = 2;
                while (!usedNames.Add(unique)) unique = $"{baseName}_{n++}";

                var req = new ConversionRequest
                {
                    InputPath = file.Path, Source = file.SelectedSource!, Settings = settings, OutputDirectory = outDir,
                    OutputBaseName = unique,
                    ConfirmOverwrite = existing => Dispatcher.UIThread.InvokeAsync(() =>
                        _dialogs.ConfirmAsync("Replace existing file?",
                            existing.Count == 1 ? $"'{Path.GetFileName(existing[0])}' already exists in the output folder. Do you want to replace it?"
                                                : $"{existing.Count} files already exist in the output folder. Do you want to replace them?",
                            "Replace", "Keep existing")).GetAwaiter().GetResult()
                };
                var result = await Task.Run(() => _conv.Convert(req, progress, cts.Token));
                lastMessage = result.Message;

                if (result.Success)
                {
                    ok++; file.Status = FileStatus.Done; file.StatusText = "Done"; file.Detail = "";
                    AddHistory(file, result, true);
                    foreach (var w in result.Warnings) _log.LogWarning("{File}: {Warning}", file.Name, w);
                }
                else if (result.Cancelled)
                {
                    cancelled++; file.Status = FileStatus.Cancelled; file.StatusText = "Cancelled";
                    AddHistory(file, result, false);
                    break;
                }
                else
                {
                    failed++; file.Status = FileStatus.Failed; file.StatusText = "Failed"; file.Detail = result.Message;
                    AddHistory(file, result, false);
                    _log.LogError("{File}: {Message}{Detail}", file.Name, result.Message, result.TechnicalDetail == null ? "" : Environment.NewLine + result.TechnicalDetail);
                }
            }
        }
        catch (Exception ex)
        {
            failed++;
            lastMessage = _files.Describe(ex);
            _log.LogError(ex, "Batch conversion failed");
        }
        finally
        {
            IsBusy = false; _convertCts = null; cts.Dispose();
            foreach (var f in targets.Where(f => f.Status == FileStatus.Converting)) { f.Status = FileStatus.Cancelled; f.StatusText = "Not run"; }
        }

        ProgressPercent = failed == 0 && cancelled == 0 ? 100 : ProgressPercent;
        ProgressText = "";
        if (cancelled > 0) { StatusText = "Cancelled"; Banner("Conversion was cancelled. Files already finished were kept.", "warning", ok > 0); }
        else if (failed == 0) { StatusText = "Done"; Banner(targets.Count == 1 ? lastMessage : $"Converted {ok} files successfully to {outDir}", "success", true); }
        else if (ok > 0) { StatusText = "Finished with errors"; Banner($"{ok} converted, {failed} failed. {lastMessage}", "warning", true); }
        else { StatusText = "Failed"; Banner(lastMessage, "error", false); }
        RaiseFileStats();
    }

    private void AddHistory(FileItem file, ConversionResult r, bool success)
    {
        var entry = new HistoryEntry
        {
            FileName = file.Name, Success = success,
            Output = r.OutputFiles.Count > 0 ? string.Join(", ", r.OutputFiles.Select(Path.GetFileName)) : "",
            Summary = success ? $"{r.RecordsProcessed:N0} records · {r.RowsWritten:N0} rows · {r.ColumnCount} columns · {r.Elapsed.TotalSeconds:0.0}s" : r.Message
        };
        History.Insert(0, entry);
        _state.State.History.Insert(0, entry);
        if (_state.State.History.Count > 100) _state.State.History.RemoveRange(100, _state.State.History.Count - 100);
        _state.Save();
    }
}
