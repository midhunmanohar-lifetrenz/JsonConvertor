using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JSONConverterPro.Models;
using Microsoft.Extensions.Logging;

namespace JSONConverterPro.Services;

/// <summary>Collects warnings and errors for the in-app error log (technical detail also goes to the log file).</summary>
public sealed class LogStore
{
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public void Add(LogLevel level, string category, string message)
    {
        if (level < LogLevel.Warning) return;
        string first = message.Split('\n')[0].Trim();
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Insert(0, new LogEntry { Level = level.ToString(), Message = first });
            while (Entries.Count > 300) Entries.RemoveAt(Entries.Count - 1);
        });
    }
}

public sealed class AppState
{
    public string? LastOutputDirectory { get; set; }
    public List<HistoryEntry> History { get; set; } = new();
}

/// <summary>Remembers the last output folder and recent conversions between runs.</summary>
public sealed class AppStateService
{
    private readonly string _path;
    public AppState State { get; private set; } = new();

    public AppStateService()
    {
        string dir = Environment.GetEnvironmentVariable("JCP_STATE_DIR") is { Length: > 0 } o ? o : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JSONConverterPro");
        _path = Path.Combine(dir, "state.json");
        try
        {
            if (File.Exists(_path)) State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path)) ?? new AppState();
        }
        catch { State = new AppState(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(State));
        }
        catch { /* non-critical */ }
    }
}

public interface IDialogService
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync(string? startFolder);
    /// <summary>Shows the system Save As dialog; returns the chosen path or null if cancelled.</summary>
    Task<string?> SaveFileAsync(string suggestedName, string extension, string typeLabel, string? startFolder);
    Task<bool> ConfirmAsync(string title, string message, string yes, string no);
}

public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (Owner == null) return Array.Empty<string>();
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose JSON files",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("JSON and text files") { Patterns = new[] { "*.json", "*.txt" } } }
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
    }

    private async Task<IStorageFolder?> StartFolder(string? path)
    {
        if (Owner == null || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        try { return await Owner.StorageProvider.TryGetFolderFromPathAsync(path); } catch { return null; }
    }

    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        if (Owner == null) return null;
        var res = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to save the converted files", AllowMultiple = false, SuggestedStartLocation = await StartFolder(startFolder)
        });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string suggestedName, string extension, string typeLabel, string? startFolder)
    {
        if (Owner == null) return null;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save converted file",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StartFolder(startFolder),
            FileTypeChoices = new[] { new FilePickerFileType(typeLabel) { Patterns = new[] { "*." + extension } } }
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string yes, string no)
    {
        if (Owner == null) return false;
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var yesBtn = new Button { Content = yes, Classes = { "primary" } };
        var noBtn = new Button { Content = no, Classes = { "soft" } };
        yesBtn.Click += (_, _) => dlg.Close(true);
        noBtn.Click += (_, _) => dlg.Close(false);
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { noBtn, yesBtn } }
            }
        };
        return await dlg.ShowDialog<bool>(Owner);
    }
}

public static class ShellService
{
    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", $"\"{folder}\"");
            else Process.Start("xdg-open", $"\"{folder}\"");
        }
        catch { /* best effort */ }
    }
}
