using System.Globalization;
using System.Text.RegularExpressions;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Helpers;

public static class FileSizeHelper
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}

public static class DateFormatHelper
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"
    };

    /// <summary>
    /// Only strict ISO-8601 values without a time-zone designator are treated as dates.
    /// Ambiguous strings (12/01/2024, 20240101, 5) and values carrying an offset stay text so nothing is altered.
    /// </summary>
    public static bool TryParseReliable(string s, out DateTime date)
    {
        date = default;
        if (s.Length < 10 || s.Length > 27 || s[4] != '-') return false;
        if (!DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        return date.Year >= 1900;
    }

    public static string ExcelFormat(DateTime d) =>
        d.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd"
        : d.Ticks % TimeSpan.TicksPerSecond != 0 ? "yyyy-mm-dd hh:mm:ss.000"
        : "yyyy-mm-dd hh:mm:ss";
}

public static class NumberHelper
{
    /// <summary>
    /// True when the number can be stored as an Excel (IEEE double) number without losing digits.
    /// Anything with more than 15 significant digits (typical for long IDs) must stay text.
    /// </summary>
    public static bool CanBeExcelNumber(string raw, out double value)
    {
        value = 0;
        int sig = 0; bool seenNonZero = false;
        foreach (char ch in raw)
        {
            if (ch == 'e' || ch == 'E') break;
            if (ch >= '0' && ch <= '9')
            {
                if (ch != '0') seenNonZero = true;
                if (seenNonZero) sig++;
            }
        }
        if (sig > 15) return false;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        return !double.IsInfinity(value) && !double.IsNaN(value);
    }

    /// <summary>
    /// Number of decimal places when a plain decimal number is written with a trailing zero ("5.0" → 1, "0.250" → 3),
    /// otherwise 0. Excel would drop those zeros, which hides the precision the source reported.
    /// </summary>
    public static int TrailingZeroDecimals(string raw)
    {
        int dot = raw.IndexOf('.');
        if (dot < 0 || raw[^1] != '0' || raw.IndexOfAny(new[] { 'e', 'E' }) >= 0) return 0;
        int dec = raw.Length - dot - 1;
        return dec <= 15 ? dec : 0;
    }
}

public static class ColumnOrderingHelper
{
    private static readonly Regex Splitter = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", RegexOptions.Compiled);

    private static int Group(string name)
    {
        // Classify on the leaf segment of a dotted path: "visits.visitId" -> "visitId"
        int dot = name.LastIndexOf('.');
        string leaf = dot >= 0 ? name[(dot + 1)..] : name;
        var tokens = Splitter.Matches(leaf).Select(m => m.Value.ToLowerInvariant()).ToList();
        string flat = leaf.ToLowerInvariant();
        if (tokens.Count == 0) return 7;

        bool Has(params string[] t) => tokens.Any(x => t.Contains(x));
        if (Has("id", "uuid", "guid", "pk")) return 0;
        if (Has("code")) return 1;
        if (Has("name", "title")) return 2;
        if (Has("description", "desc", "remarks", "comment", "notes")) return 3;
        if (Has("date", "time", "datetime", "timestamp", "dob")) return 4;
        if (Has("status", "state")) return 5;
        if (Has("type", "category", "kind", "class")) return 6;
        return 7;
    }

    /// <summary>Returns column ids in output order. Original field names are never changed.</summary>
    public static int[] Order(IReadOnlyList<JsonColumn> columns, ConversionSettings settings)
    {
        var system = columns.Where(c => c.IsSystem).OrderBy(c => c.Id).ToList();
        var data = columns.Where(c => !c.IsSystem).ToList();
        IEnumerable<JsonColumn> ordered;
        switch (settings.ColumnOrdering)
        {
            case ColumnOrdering.Alphabetical:
                ordered = data.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id);
                break;
            case ColumnOrdering.Original:
                ordered = data.OrderBy(c => c.Id);
                break;
            case ColumnOrdering.Custom:
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < settings.CustomColumnOrder.Count; i++) pos.TryAdd(settings.CustomColumnOrder[i], i);
                ordered = data.OrderBy(c => pos.TryGetValue(c.Name, out var p) ? p : int.MaxValue).ThenBy(c => c.Id);
                break;
            default:
                ordered = data.OrderBy(c => Group(c.Name)).ThenBy(c => c.Depth).ThenBy(c => c.Id);
                break;
        }
        return system.Concat(ordered).Select(c => c.Id).ToArray();
    }
}
