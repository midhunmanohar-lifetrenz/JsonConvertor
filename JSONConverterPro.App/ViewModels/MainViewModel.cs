using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using JSONConverterPro.Core.Helpers;
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
        var ps = state.State.Settings ?? new PersistedSettings();
        ApplyPersisted(ps);
        foreach (var pr in state.State.Presets) Presets.Add(pr);
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
    [ObservableProperty] private bool _protectCsv = true;
    [ObservableProperty] private string _arraySeparator = "; ";
    [ObservableProperty] private int _csvEncodingIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _pasteRowsIndex;

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
        ProtectCsvFormulas = ProtectCsv,
        ArrayJoinSeparator = string.IsNullOrEmpty(ArraySeparator) ? "; " : ArraySeparator,
        CsvEncoding = (CsvEncoding)Math.Clamp(CsvEncodingIndex, 0, 2),
        ColumnRules = _rules.Values.Where(r => !r.IsDefault).Select(r => new ColumnRule { Name = r.Name, Include = r.Include, RenameTo = r.RenameTo, Mask = r.Mask }).ToList(),
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
        SaveSettings();
    }
    partial void OnArrayModeIndexChanged(int value) { OnPropertyChanged(nameof(ArrayHint)); SaveSettings(); SchedulePreview(); }
    partial void OnOrderIndexChanged(int value) { OnPropertyChanged(nameof(IsCustomOrder)); SaveSettings(); SchedulePreview(); }
    partial void OnNullIndexChanged(int value) { SaveSettings(); SchedulePreview(); }
    partial void OnFlattenNestedChanged(bool value) { SaveSettings(); SchedulePreview(); }
    partial void OnParseDatesChanged(bool value) { SaveSettings(); SchedulePreview(); }
    partial void OnCustomOrderTextChanged(string value) { SaveSettings(); if (IsCustomOrder) SchedulePreview(); }
    partial void OnSheetNameChanged(string value) => SaveSettings();
    partial void OnDelimiterIndexChanged(int value) => SaveSettings();
    partial void OnProtectCsvChanged(bool value) => SaveSettings();
    partial void OnArraySeparatorChanged(string value) { SaveSettings(); SchedulePreview(); }
    partial void OnCsvEncodingIndexChanged(int value) => SaveSettings();
    partial void OnThemeIndexChanged(int value) { SaveSettings(); ApplyTheme(); }
    partial void OnPasteRowsIndexChanged(int value) { SaveSettings(); SchedulePasteRefresh(0); }

    /// <summary>Remembers the user's conversion options between runs.</summary>
    private void SaveSettings()
    {
        if (_loading) return;
        _state.State.Settings = CurrentPersisted();
        _state.Save();
    }
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
}
