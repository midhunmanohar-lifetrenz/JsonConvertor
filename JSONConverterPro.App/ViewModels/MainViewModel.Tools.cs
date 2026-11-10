using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;
using JSONConverterPro.Models;
using JSONConverterPro.Services;

namespace JSONConverterPro.ViewModels;

/// <summary>Editor tools, the Columns view (include / rename / mask), preview search, presets and theme.</summary>
public partial class MainViewModel
{
    // ---------------- State

    private readonly Dictionary<string, ColumnRule> _rules = new(StringComparer.Ordinal);
    private IReadOnlyList<string[]> _pasteAllRows = Array.Empty<string[]>();

    public ObservableCollection<ColumnRuleVm> ColumnRows { get; } = new();
    public ObservableCollection<PresetEntry> Presets { get; } = new();

    /// <summary>Raised with a 1-based line and byte column so the editor can move its caret to a syntax error.</summary>
    public event Action<int, int>? PasteErrorRequested;

    [ObservableProperty] private string _pasteTab = "Preview";
    [ObservableProperty] private string _pasteSearch = "";
    [ObservableProperty] private string _pasteRowsText = "";
    [ObservableProperty] private int? _pasteErrorLine;
    [ObservableProperty] private int? _pasteErrorColumn;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private PresetEntry? _selectedPreset;

    public int PasteRecordLimit => PasteRowsIndex switch { 0 => 10, 1 => 50, 2 => 200, _ => 1000 };
    public bool IsPreviewTab => PasteTab == "Preview";
    public bool IsColumnsTab => PasteTab == "Columns";
    public bool HasErrorLocation => PasteErrorLine.HasValue;
    public bool HasPresets => Presets.Count > 0;
    public string ColumnsTabTitle => $"Columns ({ColumnRows.Count})";
    public string RulesSummary
    {
        get
        {
            var active = _rules.Values.Where(r => !r.IsDefault).ToList();
            if (active.Count == 0) return "No column rules. Every column is exported as is.";
            var parts = new List<string>();
            int ex = active.Count(r => !r.Include), mk = active.Count(r => r.Include && r.Mask != MaskMode.None), rn = active.Count(r => r.Include && !string.IsNullOrWhiteSpace(r.RenameTo));
            if (ex > 0) parts.Add($"{ex} excluded");
            if (mk > 0) parts.Add($"{mk} masked");
            if (rn > 0) parts.Add($"{rn} renamed");
            return string.Join(" · ", parts) + ". Rules apply to the preview, downloads and linked sheets.";
        }
    }

    partial void OnPasteTabChanged(string value) { OnPropertyChanged(nameof(IsPreviewTab)); OnPropertyChanged(nameof(IsColumnsTab)); }
    partial void OnPasteErrorLineChanged(int? value) => OnPropertyChanged(nameof(HasErrorLocation));
    partial void OnPasteSearchChanged(string value) { ApplyPasteFilter(); PastePreviewChanged?.Invoke(); }

    [RelayCommand] private void ShowPreviewTab() => PasteTab = "Preview";
    [RelayCommand] private void ShowColumnsTab() => PasteTab = "Columns";

    // ---------------- Preview search

    private void ApplyPasteFilter()
    {
        IReadOnlyList<string[]> rows = _pasteAllRows;
        string q = PasteSearch?.Trim() ?? "";
        if (q.Length > 0) rows = rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        PastePreviewRows = rows;
        PasteRowsText = _pasteAllRows.Count == 0 ? ""
            : q.Length > 0 ? $"{rows.Count} of {_pasteAllRows.Count} rows match"
            : $"{rows.Count} row{(rows.Count == 1 ? "" : "s")} shown";
    }

    // ---------------- Columns view

    private void RebuildColumnRows(IReadOnlyList<ColumnInfo> structure)
    {
        // Keep the existing rows while the user is typing a new name; only rebuild when the column set really changed.
        if (ColumnRows.Count == structure.Count && ColumnRows.Select(c => c.Name).SequenceEqual(structure.Select(c => c.Name))) return;
        ColumnRows.Clear();
        foreach (var info in structure)
        {
            _rules.TryGetValue(info.Name, out var rule);
            ColumnRows.Add(new ColumnRuleVm(info, rule, OnRuleChanged));
        }
        OnPropertyChanged(nameof(ColumnsTabTitle));
        OnPropertyChanged(nameof(RulesSummary));
    }

    private void OnRuleChanged()
    {
        foreach (var vm in ColumnRows)
        {
            var rule = vm.ToRule();
            if (rule.IsDefault) _rules.Remove(vm.Name); else _rules[vm.Name] = rule;
        }
        OnPropertyChanged(nameof(RulesSummary));
        SchedulePreview();
    }

    [RelayCommand]
    private void IncludeAllColumns() { foreach (var vm in ColumnRows) vm.Include = true; }

    [RelayCommand]
    private void ResetRules()
    {
        _rules.Clear();
        foreach (var vm in ColumnRows) { vm.Include = true; vm.RenameTo = ""; vm.MaskIndex = 0; }
        OnPropertyChanged(nameof(RulesSummary));
        SchedulePreview();
    }

    // ---------------- Editor tools

    [RelayCommand]
    private void FormatPaste()
    {
        if (string.IsNullOrWhiteSpace(PasteText)) return;
        try
        {
            using var doc = JsonDocument.Parse(PasteText, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            PasteText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            Banner("Can't format this text. It has a syntax error, or it is JSON Lines (one value per line), which stays as is.", "warning", false);
        }
    }

    [RelayCommand]
    private void GoToError()
    {
        if (PasteErrorLine is int l) PasteErrorRequested?.Invoke(l, PasteErrorColumn ?? 1);
    }

    // ---------------- Persisted settings and presets

    private PersistedSettings CurrentPersisted() => new()
    {
        IsExcel = IsExcel, SheetName = SheetName, FlattenNested = FlattenNested, ParseDates = ParseDates, ProtectCsv = ProtectCsv,
        ArrayModeIndex = ArrayModeIndex, OrderIndex = OrderIndex, NullIndex = NullIndex, DelimiterIndex = DelimiterIndex,
        CustomOrderText = CustomOrderText, ArraySeparator = ArraySeparator, CsvEncodingIndex = CsvEncodingIndex,
        ThemeIndex = ThemeIndex, PasteRowsIndex = PasteRowsIndex
    };

    private void ApplyPersisted(PersistedSettings ps)
    {
        IsExcel = ps.IsExcel; SheetName = string.IsNullOrWhiteSpace(ps.SheetName) ? "Data" : ps.SheetName;
        FlattenNested = ps.FlattenNested; ParseDates = ps.ParseDates; ProtectCsv = ps.ProtectCsv;
        ArrayModeIndex = Math.Clamp(ps.ArrayModeIndex, 0, 3); OrderIndex = Math.Clamp(ps.OrderIndex, 0, 3);
        NullIndex = Math.Clamp(ps.NullIndex, 0, 1); DelimiterIndex = Math.Clamp(ps.DelimiterIndex, 0, 2);
        CustomOrderText = ps.CustomOrderText ?? "";
        ArraySeparator = string.IsNullOrEmpty(ps.ArraySeparator) ? "; " : ps.ArraySeparator;
        CsvEncodingIndex = Math.Clamp(ps.CsvEncodingIndex, 0, 2);
        PasteRowsIndex = Math.Clamp(ps.PasteRowsIndex, 0, 3);
        // Theme is a personal preference, not part of a feed preset, so presets keep the current theme (see ApplyPreset).
        ThemeIndex = Math.Clamp(ps.ThemeIndex, 0, 2);
    }

    [RelayCommand]
    private void SavePreset()
    {
        string name = PresetName.Trim();
        if (name.Length == 0) { Banner("Type a name for the preset first.", "warning", false); return; }
        var entry = new PresetEntry
        {
            Name = name, Settings = CurrentPersisted(),
            Rules = _rules.Values.Where(r => !r.IsDefault).Select(r => new ColumnRule { Name = r.Name, Include = r.Include, RenameTo = r.RenameTo, Mask = r.Mask }).ToList()
        };
        var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Presets.Remove(existing);
        Presets.Add(entry);
        _state.State.Presets = Presets.ToList(); _state.Save();
        OnPropertyChanged(nameof(HasPresets));
        SelectedPreset = entry; PresetName = "";
        Banner($"Preset '{name}' saved. It includes your options and {entry.Rules.Count} column rule(s).", "success", false);
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        var p = SelectedPreset;
        if (p == null) { Banner("Choose a preset to apply.", "warning", false); return; }
        int theme = ThemeIndex;
        ApplyPersisted(p.Settings);
        ThemeIndex = theme;
        _rules.Clear();
        foreach (var r in p.Rules) _rules[r.Name] = new ColumnRule { Name = r.Name, Include = r.Include, RenameTo = r.RenameTo, Mask = r.Mask };
        ColumnRows.Clear();                       // force the Columns view to rebuild with the preset's rules
        OnPropertyChanged(nameof(RulesSummary)); OnPropertyChanged(nameof(ColumnsTabTitle));
        SaveSettings();
        SchedulePasteRefresh(0); SchedulePreview();
        Banner($"Preset '{p.Name}' applied.", "success", false);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var p = SelectedPreset;
        if (p == null) return;
        Presets.Remove(p);
        _state.State.Presets = Presets.ToList(); _state.Save();
        OnPropertyChanged(nameof(HasPresets));
        SelectedPreset = null;
    }

    // ---------------- Theme

    public void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = ThemeIndex switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
}
