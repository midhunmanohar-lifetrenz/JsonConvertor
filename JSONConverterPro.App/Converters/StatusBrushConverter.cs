using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using JSONConverterPro.Models;

namespace JSONConverterPro.Converters;

/// <summary>Maps a file status to chip colours. ConverterParameter: "bg" or "fg".</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value is FileStatus fs ? fs : FileStatus.Analyzing;
        bool bg = (parameter as string) == "bg";
        (string back, string fore) = s switch
        {
            FileStatus.Ready => ("#DDF3E8", "#11704A"),
            FileStatus.Done => ("#C9F0DC", "#0B5B3A"),
            FileStatus.NeedsSelection => ("#FFF1D6", "#8A5A00"),
            FileStatus.Invalid or FileStatus.Failed => ("#FDE8E6", "#B3261E"),
            FileStatus.Converting => ("#DCEBFF", "#1A4FA3"),
            FileStatus.Cancelled => ("#ECECEC", "#555555"),
            _ => ("#DDEEF0", "#0B3C49")
        };
        return new SolidColorBrush(Color.Parse(bg ? back : fore));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
