using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
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
    private CancellationTokenSource? _previewCts, _convertCts, _debounceCts, _pasteCts, _pasteDebounceCts;
    private bool _loading = true;

    public MainViewModel(JsonSchemaService schema, ConversionService conv, PreviewService preview, FileService files,
        IDialogService dialogs, AppStateService state, LogStore logStore, ILogger<MainViewModel> log)
    {
        _schema = schema; _conv = conv; _preview = preview; _files = files; _dialogs = dialogs; _state = state; _log = log;
        LogEntries = logStore.Entries;
        foreach (var h in state.State.History.Take(100)) History.Add(h);
        LastOutputFolder = state.State.LastOutputDirectory ?? "";
        Version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        TryCleanTempDir();
        _loading = false;
    }

    // ---------------- State

    public string Version { get; }
    public ObservableCollection<FileItem> Files { get; } = new();
    public ObservableCollection<DataSourceCandidate> Candidates { get; } = new();
    public ObservableCollection<DataSourceCandidate> PasteCandidates { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; }
    public string LogDirectory => FileLoggerProvider.DefaultDirectory;

    public event Action? PreviewChanged;
    public event Action? PastePreviewChanged;
    public IReadOnlyList<string> PreviewColumns { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string[]> PreviewRows { get; private set; } = Array.Empty<string[]>();
    public IReadOnlyList<string> PastePreviewColumns { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string[]> PastePreviewRows { get; private set; } = Array.Empty<string[]>();

    [ObservableProperty] private string _currentPage = "Convert";
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

    /// <summary>Last folder a file was saved to. Only used to start the next dialog there; it is never shown as a default path.</summary>
    [ObservableProperty] private string _lastOutputFolder = "";

    // Progress and feedback
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerKind = "info";
    [ObservableProperty] private bool _bannerHasFolder;

    // File preview (Files / Preview pages)
    [ObservableProperty] private bool _previewBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewTotalsText = "";
    [ObservableProperty] private string _previewMessage = "";
    [ObservableProperty] private string _previewWarnings = "";
    [ObservableProperty] private int _previewColumnCount;
    [ObservableProperty] private long _previewRecordCount;

    // Paste area (Convert page)
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private DataSourceCandidate? _selectedPasteCandidate;
    [ObservableProperty] private string _pasteStatusText = "Paste JSON above to see a live preview.";
    [ObservableProperty] private string _pasteStatusKind = "idle";
    [ObservableProperty] private bool _pasteHasPreview;
    [ObservableProperty] private string _pasteWarnings = "";

    public bool HasFiles => Files.Count > 0;
    public bool IsCsv { get => !IsExcel; set => IsExcel = !value; }
    public bool ShowCandidates => Candidates.Count > 1;
    public bool ShowPasteCandidates => PasteCandidates.Count > 1;
    public bool IsConvertPage => CurrentPage == "Convert";
    public bool IsFilesPage => CurrentPage == "Files";
    public bool IsPreviewPage => CurrentPage == "Preview";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsHistoryPage => CurrentPage == "History";
    public bool IsLogPage => CurrentPage == "Log";
    public bool ShowFilesBar => IsFilesPage || IsPreviewPage;
    public bool IsCustomOrder => OrderIndex == 3;
    public bool ShowDelimiter => !IsExcel;
    public bool ShowSheetName => IsExcel;
    public bool PasteIsOk => PasteStatusKind == "ok";
    public bool PasteIsError => PasteStatusKind == "error";
    public bool PasteIsWarn => PasteStatusKind is "warn" or "busy";
    public string FilesCountText => Files.Count.ToString("N0");
    public string RecordsTotalText => Files.Sum(f => f.SelectedSource?.Count ?? 0).ToString("N0");
    public string ColumnsText => HasPreview ? PreviewColumnCount.ToString("N0") : "–";
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
        foreach (var n in new[] { nameof(IsConvertPage), nameof(IsFilesPage), nameof(IsPreviewPage), nameof(IsSettingsPage), nameof(IsHistoryPage), nameof(IsLogPage), nameof(ShowFilesBar) })
            OnPropertyChanged(n);
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
    partial void OnPreviewColumnCountChanged(int value) => OnPropertyChanged(nameof(ColumnsText));
    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(ColumnsText));
    partial void OnPasteStatusKindChanged(string value)
    {
        OnPropertyChanged(nameof(PasteIsOk)); OnPropertyChanged(nameof(PasteIsError)); OnPropertyChanged(nameof(PasteIsWarn));
    }

    // ---------------- Banner

    public void ShowError(string text) => Banner(text, "error", false);
    private void Banner(string text, string kind, bool folder)
    {
        BannerText = text; BannerKind = kind; BannerHasFolder = folder; ShowBanner = true;
    }

    [RelayCommand] private void DismissBanner() => ShowBanner = false;
    [RelayCommand] private void Navigate(string page) => CurrentPage = page;

    // ============================================================
    //  Paste area (the first screen)
    // ============================================================

    private static string TempDir => Path.Combine(Path.GetTempPath(), "JSONConverterPro", "paste");
    private static void TryCleanTempDir() { try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { /* locked: ignore */ } }
    private static void TryDelete(string? path) { if (path != null) FileService.TryDelete(path); }

    private string? _pasteFile;                 // last successfully analysed copy of the text
    private DataSourceCandidate? _pasteSource;  // data array that the preview and downloads use
    private string? _pasteChosenKey;            // user's explicit array choice, kept across edits
    private bool _suppressPasteCandidate;
    private int _pasteSeq;

    private static string Key(DataSourceCandidate c) => c.Kind + ":" + c.Path;

    partial void OnPasteTextChanged(string value) => SchedulePasteRefresh();

    partial void OnSelectedPasteCandidateChanged(DataSourceCandidate? value)
    {
        if (_suppressPasteCandidate || value == null) return;
        _pasteChosenKey = Key(value);
        SchedulePasteRefresh(0);
    }

    private void SchedulePasteRefresh(int delayMs = 450)
    {
        if (_loading) return;
        _pasteDebounceCts?.Cancel();
        var cts = _pasteDebounceCts = new CancellationTokenSource();
        _ = Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(() => _ = RefreshPasteAsync());
        });
    }

    private void ClearPastePreview(string status, string kind)
    {
        PastePreviewColumns = Array.Empty<string>(); PastePreviewRows = Array.Empty<string[]>();
        PasteHasPreview = false; PasteWarnings = "";
        _pasteSource = null;
        PasteStatusText = status; PasteStatusKind = kind;
        PastePreviewChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearPaste()
    {
        _pasteCts?.Cancel();
        _pasteChosenKey = null;
        PasteText = "";
        _suppressPasteCandidate = true; PasteCandidates.Clear(); SelectedPasteCandidate = null; _suppressPasteCandidate = false;
        OnPropertyChanged(nameof(ShowPasteCandidates));
        TryDelete(_pasteFile); _pasteFile = null;
        ClearPastePreview("Paste JSON above to see a live preview.", "idle");
    }

    public async Task RefreshPasteAsync()
    {
        _pasteCts?.Cancel();
        string text = PasteText;
        if (string.IsNullOrWhiteSpace(text))
        {
            TryDelete(_pasteFile); _pasteFile = null;
            _suppressPasteCandidate = true; PasteCandidates.Clear(); SelectedPasteCandidate = null; _suppressPasteCandidate = false;
            OnPropertyChanged(nameof(ShowPasteCandidates));
            ClearPastePreview("Paste JSON above to see a live preview.", "idle");
            return;
        }

        var cts = _pasteCts = new CancellationTokenSource();
        var ct = cts.Token;
        var settings = ToSettings();
        string chosenKey = _pasteChosenKey ?? "";
        string file = Path.Combine(TempDir, $"paste-{++_pasteSeq}.json");
        PasteStatusText = "Checking…"; PasteStatusKind = "busy";
        try
        {
            var analysis = await Task.Run(() =>
            {
                Directory.CreateDirectory(TempDir);
                File.WriteAllText(file, text, new UTF8Encoding(false));
                using var s = _files.OpenRead(file);
                var info = _schema.Analyze(s, ct);
                if (info.IsEmpty) throw new UserFacingException("There's no JSON in the text yet.");
                if (info.IsUnsupportedRoot) throw new UserFacingException("The JSON must be an object or an array to convert it.");
                return info;
            }, ct);
            if (ct.IsCancellationRequested) { TryDelete(file); return; }

            _suppressPasteCandidate = true;
            PasteCandidates.Clear();
            foreach (var c in analysis.Candidates) PasteCandidates.Add(c);
            DataSourceCandidate? chosen = analysis.Candidates.FirstOrDefault(c => Key(c) == chosenKey);
            chosen ??= analysis.RequiresSelection ? null : analysis.Suggested;
            SelectedPasteCandidate = chosen;
            _suppressPasteCandidate = false;
            OnPropertyChanged(nameof(ShowPasteCandidates));

            if (chosen == null)
            {
                TryDelete(file);
                ClearPastePreview("Several data arrays found. Choose the one to convert.", "warn");
                return;
            }

            var result = await Task.Run(() => _preview.Build(file, chosen, settings, 10, ct), ct);
            if (ct.IsCancellationRequested) { TryDelete(file); return; }

            TryDelete(_pasteFile);
            _pasteFile = file; _pasteSource = chosen;
            PastePreviewColumns = result.ColumnNames; PastePreviewRows = result.Rows;
            PasteHasPreview = true;
            PasteWarnings = string.Join("  ", result.Warnings.Take(3));
            PasteStatusText = $"Valid · {result.TotalRecords:N0} record{(result.TotalRecords == 1 ? "" : "s")} · {result.TotalColumns:N0} columns" +
                              (result.ChildTables.Count > 0 ? $" · {result.ChildTables.Count} linked sheet(s)" : "");
            PasteStatusKind = "ok";
            PastePreviewChanged?.Invoke();
        }
        catch (OperationCanceledException) { TryDelete(file); }
        catch (Exception ex)
        {
            TryDelete(file);
            if (ct.IsCancellationRequested) return;
            _suppressPasteCandidate = false;
            ClearPastePreview(_files.Describe(ex), "error");
        }
    }

    /// <summary>Loads a small file straight into the paste area (used when a single file is dropped on the first screen).</summary>
    public async Task LoadFileIntoEditorAsync(string path)
    {
        try
        {
            string text = await Task.Run(() =>
            {
                _files.ValidateInput(path);
                using var s = _files.OpenRead(path);
                using var r = new StreamReader(s, new UTF8Encoding(false), true);
                return r.ReadToEnd();
            });
            _pasteChosenKey = null;
            CurrentPage = "Convert";
            PasteText = text;
        }
        catch (Exception ex)
        {
            Banner(_files.Describe(ex), "error", false);
            _log.LogWarning(ex, "Could not load {File} into the editor", path);
        }
    }

    [RelayCommand] private Task DownloadExcelAsync() => DownloadAsync(OutputFormat.Excel);
    [RelayCommand] private Task DownloadCsvAsync() => DownloadAsync(OutputFormat.Csv);

    private async Task DownloadAsync(OutputFormat format)
    {
        if (IsBusy) return;
        if (_pasteFile == null || _pasteSource == null || !PasteIsOk)
        {
            Banner(PasteIsError ? "Fix the JSON first. The problem is shown above the preview." : "Paste some valid JSON first.", "warning", false);
            return;
        }
        string ext = format == OutputFormat.Excel ? "xlsx" : "csv";
        string? target = await _dialogs.SaveFileAsync("converted", ext, format == OutputFormat.Excel ? "Excel workbook" : "CSV file",
            string.IsNullOrEmpty(LastOutputFolder) ? null : LastOutputFolder);
        if (string.IsNullOrEmpty(target)) return;   // user cancelled the dialog

        string dir = Path.GetDirectoryName(target)!;
        string baseName = Path.GetFileNameWithoutExtension(target);
        // Work from a private snapshot so editing the text while saving can't disturb the running conversion.
        string snapshot = Path.Combine(TempDir, $"download-{Guid.NewGuid():N}.json");
        try { File.Copy(_pasteFile, snapshot, true); }
        catch (Exception ex) { Banner(_files.Describe(ex), "error", false); return; }

        var settings = ToSettings();
        settings.OutputFormat = format;
        var src = _pasteSource;
        IsBusy = true; ShowBanner = false; ProgressPercent = 0; StatusText = "Saving…"; ProgressText = "";
        var cts = _convertCts = new CancellationTokenSource();
        var progress = new Progress<ConversionProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            ProgressText = p.Records > 0 ? $"{p.Phase} {p.Records:N0} records" : p.Phase;
        });
        ConversionResult result;
        try
        {
            string primary = Path.Combine(dir, baseName + "." + ext);
            var req = new ConversionRequest
            {
                InputPath = snapshot, Source = src, Settings = settings, OutputDirectory = dir, OutputBaseName = baseName,
                // The Save As dialog already confirmed the main file; only ask about additional files (linked CSV tables).
                ConfirmOverwrite = existing =>
                {
                    var extra = existing.Where(f => !string.Equals(f, primary, StringComparison.OrdinalIgnoreCase)).ToList();
                    return extra.Count == 0 || Dispatcher.UIThread.InvokeAsync(() => _dialogs.ConfirmAsync("Replace existing files?",
                        $"{extra.Count} other file(s) with matching names already exist in that folder. Do you want to replace them?", "Replace", "Keep existing")).GetAwaiter().GetResult();
                }
            };
            result = await Task.Run(() => _conv.Convert(req, progress, cts.Token));
        }
        finally
        {
            IsBusy = false; _convertCts = null; cts.Dispose();
            TryDelete(snapshot);
        }

        var entry = new FileItem(snapshot, "Pasted JSON");
        if (result.Success)
        {
            LastOutputFolder = dir; _state.State.LastOutputDirectory = dir; _state.Save();
            AddHistory(entry, result, true);
            foreach (var w in result.Warnings) _log.LogWarning("Pasted JSON: {Warning}", w);
            StatusText = "Saved"; ProgressPercent = 100; ProgressText = "";
            string names = string.Join(", ", result.OutputFiles.Select(Path.GetFileName));
            Banner($"Saved {result.RecordsProcessed:N0} records to {names}", "success", true);
        }
        else if (result.Cancelled) { StatusText = "Cancelled"; Banner("Saving was cancelled. No file was kept.", "warning", false); }
        else
        {
            StatusText = "Failed"; AddHistory(entry, result, false);
            Banner(result.Message, "error", false);
            _log.LogError("Pasted JSON: {Message}{Detail}", result.Message, result.TechnicalDetail == null ? "" : Environment.NewLine + result.TechnicalDetail);
        }
    }

    // ============================================================
    //  Files (batch) pages
    // ============================================================

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

    // ---------------- File preview

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
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                _ = RefreshPreviewAsync();
                if (!string.IsNullOrWhiteSpace(PasteText)) _ = RefreshPasteAsync();   // settings affect the paste preview too
            });
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

    // ---------------- Misc commands

    [RelayCommand] private void OpenOutputFolder() { if (!string.IsNullOrEmpty(LastOutputFolder)) ShellService.OpenFolder(LastOutputFolder); }
    [RelayCommand] private void OpenLogFolder() => ShellService.OpenFolder(LogDirectory);
    [RelayCommand] private void ClearLog() => LogEntries.Clear();
    [RelayCommand] private void ClearHistory() { History.Clear(); _state.State.History.Clear(); _state.Save(); }

    // ---------------- Batch conversion (Files page)

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

        // No default location: the user picks where the files go every time.
        string? outDir = await _dialogs.PickFolderAsync(string.IsNullOrEmpty(LastOutputFolder) ? null : LastOutputFolder);
        if (string.IsNullOrEmpty(outDir)) return;

        IsBusy = true; ShowBanner = false; ProgressPercent = 0;
        var cts = _convertCts = new CancellationTokenSource();
        var settings = ToSettings();
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

                string baseName = file.OutputBaseName;
                string unique = baseName; int n = 2;
                while (!usedNames.Add(unique)) unique = $"{baseName}_{n++}";

                var req = new ConversionRequest
                {
                    InputPath = file.Path, Source = file.SelectedSource!, Settings = settings, OutputDirectory = outDir,
                    OutputBaseName = unique,
                    ConfirmOverwrite = existing => Dispatcher.UIThread.InvokeAsync(() =>
                        _dialogs.ConfirmAsync("Replace existing file?",
                            existing.Count == 1 ? $"'{Path.GetFileName(existing[0])}' already exists in that folder. Do you want to replace it?"
                                                : $"{existing.Count} files already exist in that folder. Do you want to replace them?",
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

        if (ok > 0) { LastOutputFolder = outDir; _state.State.LastOutputDirectory = outDir; _state.Save(); }
        ProgressPercent = failed == 0 && cancelled == 0 ? 100 : ProgressPercent;
        ProgressText = "";
        if (cancelled > 0) { StatusText = "Cancelled"; Banner("Conversion was cancelled. Files already finished were kept.", "warning", ok > 0); }
        else if (failed == 0) { StatusText = "Done"; Banner(targets.Count == 1 ? lastMessage : $"Converted {ok} files successfully.", "success", true); }
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
