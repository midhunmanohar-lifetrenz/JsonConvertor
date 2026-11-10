using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JSONConverterPro.Models;
using JSONConverterPro.ViewModels;

namespace JSONConverterPro.Views;

public partial class MainWindow : Window
{
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
        if (_vm != null) { _vm.PreviewChanged -= RebuildGrid; _vm.PropertyChanged -= OnVmChanged; }
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;
        _vm.PreviewChanged += RebuildGrid;
        _vm.PropertyChanged += OnVmChanged;
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
    private void RebuildGrid()
    {
        var grid = this.FindControl<DataGrid>("PreviewGrid");
        if (grid == null || _vm == null) return;
        grid.ItemsSource = null;
        grid.Columns.Clear();
        var cols = _vm.PreviewColumns;
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
        grid.ItemsSource = _vm.PreviewRows;
    }

    // ---- Paste shortcut: Ctrl+V / Cmd+V adds JSON from the clipboard unless a text box has focus

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!mod || e.Key != Key.V || _vm == null) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        string? text = null;
        try { if (Clipboard != null) text = await Clipboard.GetTextAsync(); } catch { /* clipboard unavailable */ }
        if (string.IsNullOrWhiteSpace(text)) { _vm.ShowError("The clipboard doesn't contain any text. Copy some JSON first."); return; }
        await _vm.AddPastedJsonAsync(text);
    }

    // ---- Drag and drop

    private static bool HasFiles(DragEventArgs e) => e.Data.Contains(DataFormats.Files);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        this.FindControl<Border>("DropZone")?.Classes.Set("hot", HasFiles(e));
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) =>
        this.FindControl<Border>("DropZone")?.Classes.Set("hot", false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        this.FindControl<Border>("DropZone")?.Classes.Set("hot", false);
        if (_vm == null) return;
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths is { Count: > 0 })
        {
            _vm.CurrentPage = "Files";
            await _vm.AddPathsAsync(paths);
        }
        e.Handled = true;
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
