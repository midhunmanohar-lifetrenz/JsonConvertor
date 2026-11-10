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
                    Banner($"'{item.Name}' has several data arrays. Select it and choose the one to export below the file list.", "warning", false);
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
            if (pending != null) Banner($"Choose the data array for '{pending.Name}' below the file list first.", "warning", false);
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
