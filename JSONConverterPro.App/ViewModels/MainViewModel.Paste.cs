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

public partial class MainViewModel
{
    // ============================================================
    //  Paste area (the first screen)
    // ============================================================

    public const int MaxEditorChars = 5_000_000;
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "JSONConverterPro", "paste");
    /// <summary>
    /// Private to this running copy of the app (named after its process id), so two open windows can never
    /// read or delete each other's pasted data.
    /// </summary>
    public static string TempDir { get; } = Path.Combine(TempRoot, "s-" + Environment.ProcessId);
    /// <summary>Called when the app quits: stops work and removes every temporary copy of pasted data.</summary>
    public void Shutdown()
    {
        _pasteDebounceCts?.Cancel(); _pasteCts?.Cancel(); _previewCts?.Cancel(); _convertCts?.Cancel();
        TryDelete(_pasteFile); _pasteFile = null;
        TryDeleteDir(TempDir);
    }

    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* locked: ignore */ } }

    /// <summary>At startup: removes this copy's folder and any folder left behind by a copy that is no longer running.</summary>
    private static void TryCleanTempDir()
    {
        TryDeleteDir(TempDir);
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var f in Directory.GetFiles(TempRoot)) FileService.TryDelete(f);   // files from versions before 1.0.1
            string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                string name = Path.GetFileName(dir);
                bool alive = false;
                if (name.StartsWith("s-", StringComparison.Ordinal) && int.TryParse(name.AsSpan(2), out int pid))
                {
                    try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited && p.ProcessName == self; }
                    catch { alive = false; }
                }
                if (!alive) TryDeleteDir(dir);
            }
        }
        catch { /* best effort */ }
    }
    private static void TryDelete(string? path) { if (path != null) FileService.TryDelete(path); }

    private string? _pasteFile;                 // last successfully analysed copy of the text
    private DataSourceCandidate? _pasteSource;  // data array that the preview and downloads use
    private string? _pasteChosenKey;            // user's explicit array choice, kept across edits
    private bool _suppressPasteCandidate;
    private int _pasteSeq;

    private static string Key(DataSourceCandidate c) => c.Kind + ":" + c.Path;

    partial void OnPasteTextChanged(string value)
    {
        if (_loading) return;
        if (value.Length > MaxEditorChars) { _ = MoveLargePasteToFilesAsync(value); return; }
        // The preview no longer matches the text, so downloads are blocked until it has been checked again.
        if (!string.IsNullOrWhiteSpace(value)) { PasteStatusText = "Checking…"; PasteStatusKind = "busy"; }
        SchedulePasteRefresh();
    }

    private int _largeSeq;

    /// <summary>
    /// Very large text makes the editor slow, so it is saved to a private temporary file and handled on the Files page,
    /// which streams from disk. The file is deleted when the app quits, like all pasted data.
    /// </summary>
    public async Task MoveLargePasteToFilesAsync(string text)
    {
        try
        {
            string path = Path.Combine(TempDir, $"pasted-text-{++_largeSeq}.json");
            await Task.Run(() => { Directory.CreateDirectory(TempDir); File.WriteAllText(path, text, new UTF8Encoding(false)); });
            if (PasteText.Length > MaxEditorChars) PasteText = "";
            CurrentPage = "Files";
            await AddPathsAsync(new[] { path });
            Banner($"That text is very large ({FileSizeHelper.Format(text.Length)}), so it was added to the Files page instead of the editor. Choose Excel or CSV below and click Convert files.", "info", false);
        }
        catch (Exception ex)
        {
            Banner(_files.Describe(ex), "error", false);
            _log.LogWarning(ex, "Could not move a large paste to the Files page");
        }
    }

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
        _pasteAllRows = Array.Empty<string[]>(); ColumnRows.Clear(); OnPropertyChanged(nameof(ColumnsTabTitle)); PasteRowsText = "";
        PasteErrorLine = null; PasteErrorColumn = null;
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

            var result = await Task.Run(() => _preview.Build(file, chosen, settings, PasteRecordLimit, ct), ct);
            if (ct.IsCancellationRequested) { TryDelete(file); return; }

            TryDelete(_pasteFile);
            _pasteFile = file; _pasteSource = chosen;
            PastePreviewColumns = result.ColumnNames; _pasteAllRows = result.Rows;
            PasteErrorLine = null; PasteErrorColumn = null;
            ApplyPasteFilter();
            RebuildColumnRows(result.Structure);
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
            if (ex is UserFacingException { Line: not null } located) { PasteErrorLine = located.Line; PasteErrorColumn = located.Column; }
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
}
