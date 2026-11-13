using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JSONConverterPro.Models;
using JSONConverterPro.ViewModels;

namespace JSONConverterPro.Views;

public partial class MainWindow : Window
{
    private const long MaxEditorFileBytes = 2 * 1024 * 1024;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) => Hook();
        KeyDown += OnKeyDown;
    }

    private void Hook()
    {
        if (_vm != null) { _vm.PreviewChanged -= RebuildFileGrid; _vm.PastePreviewChanged -= RebuildPasteGrid; _vm.PropertyChanged -= OnVmChanged; _vm.PasteErrorRequested -= JumpToError; }
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;
        _vm.PreviewChanged += RebuildFileGrid;
        _vm.PastePreviewChanged += RebuildPasteGrid;
        _vm.PropertyChanged += OnVmChanged;
        _vm.PasteErrorRequested += JumpToError;
        UpdateBanner();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.BannerKind)) UpdateBanner();
    }

    private void UpdateBanner()
    {
        var banner = this.FindControl<Border>("BannerBorder");
        if (banner == null || _vm == null) return;
        banner.Classes.Set("err", _vm.BannerKind == "error");
        banner.Classes.Set("warn", _vm.BannerKind is "warning" or "info");
    }

    /// <summary>The preview columns are dynamic, so they are rebuilt from the detected structure each time.</summary>
    private static void Rebuild(DataGrid? grid, IReadOnlyList<string> cols, IReadOnlyList<string[]> rows)
    {
        if (grid == null) return;
        grid.ItemsSource = null;
        grid.Columns.Clear();
        for (int i = 0; i < cols.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = cols[i],
                Binding = new Binding($"[{i}]"),
                MinWidth = 90,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }
        grid.ItemsSource = rows;
    }

    private void RebuildFileGrid() { if (_vm != null) Rebuild(this.FindControl<DataGrid>("PreviewGrid"), _vm.PreviewColumns, _vm.PreviewRows); }
    private void RebuildPasteGrid() { if (_vm != null) Rebuild(this.FindControl<DataGrid>("PastePreviewGrid"), _vm.PastePreviewColumns, _vm.PastePreviewRows); }

    /// <summary>Moves the editor caret to a syntax error. The parser reports a byte column, so it is converted to a character index.</summary>
    private void JumpToError(int line, int byteColumn)
    {
        var box = this.FindControl<TextBox>("PasteBox");
        if (box?.Text is not { } text) return;
        int index = 0, current = 1;
        while (current < line && index < text.Length)
        {
            int nl = text.IndexOf('\n', index);
            if (nl < 0) { index = text.Length; break; }
            index = nl + 1; current++;
        }
        int bytes = 0;
        while (index < text.Length && text[index] != '\n' && bytes < byteColumn - 1)
        {
            bytes += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(index, char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1));
            index += char.IsHighSurrogate(text[index]) ? 2 : 1;
        }
        index = Math.Min(index, text.Length);
        box.Focus();
        box.SelectionStart = index;
        box.SelectionEnd = Math.Min(index + 1, text.Length);
        box.CaretIndex = index;
    }

    // ---- Paste shortcut: Ctrl+V / Cmd+V anywhere (outside a text box) puts the clipboard text into the paste area

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!mod || e.Key != Key.V || _vm == null) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;   // a text box handles its own paste
        e.Handled = true;
        string? text = null;
        try { if (Clipboard != null) text = await Clipboard.TryGetTextAsync(); } catch { /* clipboard unavailable */ }
        if (string.IsNullOrWhiteSpace(text)) { _vm.ShowError("The clipboard doesn't contain any text. Copy some JSON first."); return; }
        if (text.Length > MainViewModel.MaxEditorChars) { await _vm.MoveLargePasteToFilesAsync(text); return; }
        _vm.CurrentPage = "Convert";
        _vm.PasteText = text;
    }

    // ---- Drag and drop

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        this.FindControl<Border>("DropZone")?.Classes.Set("hot", HasFiles(e));
        this.FindControl<Border>("EditorCard")?.Classes.Set("hot", HasFiles(e));
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        this.FindControl<Border>("DropZone")?.Classes.Set("hot", false);
        this.FindControl<Border>("EditorCard")?.Classes.Set("hot", false);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        OnDragLeave(null, e);
        if (_vm == null) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        e.Handled = true;
        if (paths is not { Count: > 0 }) return;

        // One small file dropped on the first screen goes straight into the paste area; everything else joins the file list.
        if (_vm.IsConvertPage && paths.Count == 1 && new FileInfo(paths[0]).Length <= MaxEditorFileBytes
            && JSONConverterPro.Core.Services.FileService.SupportedExtensions.Contains(Path.GetExtension(paths[0]), StringComparer.OrdinalIgnoreCase))
        {
            await _vm.LoadFileIntoEditorAsync(paths[0]);
            return;
        }
        _vm.CurrentPage = "Files";
        await _vm.AddPathsAsync(paths);
    }

    // ---- Row handlers (item templates carry the FileItem in Tag)

    private void OnFileRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: FileItem item }) _vm?.SelectFile(item);
    }

    private void OnRemoveFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: FileItem item }) _vm?.RemoveFile(item);
        e.Handled = true;
    }
}
