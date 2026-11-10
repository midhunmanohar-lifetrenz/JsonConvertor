using CommunityToolkit.Mvvm.ComponentModel;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Models;

public enum FileStatus { Analyzing, Ready, NeedsSelection, Invalid, Converting, Done, Failed, Cancelled }

public partial class FileItem : ObservableObject
{
    public FileItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
        SizeText = FileSizeHelper.Format(SizeBytes);
    }

    public string Path { get; }
    public string Name { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }

    [ObservableProperty] private FileStatus _status = FileStatus.Analyzing;
    [ObservableProperty] private string _statusText = "Analyzing…";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _recordsText = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private JsonStructureInfo? _structure;
    [ObservableProperty] private DataSourceCandidate? _selectedSource;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
}

public sealed class HistoryEntry
{
    public string FileName { get; set; } = "";
    public string Output { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Success { get; set; }
    public DateTime When { get; set; } = DateTime.Now;
    public string WhenText => When.ToString("dd MMM yyyy HH:mm");
}

public sealed class LogEntry
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
    public string WhenText => When.ToString("HH:mm:ss");
}
