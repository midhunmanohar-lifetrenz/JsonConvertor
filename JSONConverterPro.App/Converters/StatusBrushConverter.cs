using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using JSONConverterPro.Models;

namespace JSONConverterPro.Converters;

/// <summary>Maps a file status to chip colours from the active theme. ConverterParameter: "bg" or "fg".</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value is FileStatus fs ? fs : FileStatus.Analyzing;
        bool bg = (parameter as string) == "bg";
        string key = s switch
        {
            FileStatus.Ready or FileStatus.Done => "Ok",
            FileStatus.NeedsSelection => "Warn",
            FileStatus.Invalid or FileStatus.Failed => "Err",
            FileStatus.Converting => "Info",
            FileStatus.Cancelled => "Neutral",
            _ => "Chip"
        };
        string res = key == "Chip" ? (bg ? "ChipBrush" : "InkBrush") : key + (bg ? "BgBrush" : "Brush");
        var app = Application.Current;
        if (app != null && app.TryGetResource(res, app.ActualThemeVariant, out var brush) && brush is IBrush b) return b;
        return bg ? Brushes.LightGray : Brushes.Black;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
