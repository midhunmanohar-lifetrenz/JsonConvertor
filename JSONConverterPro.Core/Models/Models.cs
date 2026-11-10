namespace JSONConverterPro.Core.Models;

public enum OutputFormat { Excel, Csv }
public enum ArrayHandling { Join, JsonText, ExpandRows, SeparateSheets }
public enum ColumnOrdering { Smart, Alphabetical, Original, Custom }
public enum NullHandling { Blank, LiteralNull }
public enum CsvDelimiter { Comma, Semicolon, Tab }

/// <summary>User-facing conversion options. Cloned per conversion so UI edits never leak into a running job.</summary>
public sealed class ConversionSettings
{
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Excel;
    public string SheetName { get; set; } = "Data";
    /// <summary>When false, nested objects are kept as one JSON text cell instead of dotted columns.</summary>
    public bool FlattenNestedObjects { get; set; } = true;
    public ArrayHandling ArrayHandling { get; set; } = ArrayHandling.Join;
    public ColumnOrdering ColumnOrdering { get; set; } = ColumnOrdering.Smart;
    public List<string> CustomColumnOrder { get; set; } = new();
    public NullHandling NullHandling { get; set; } = NullHandling.Blank;
    public CsvDelimiter CsvDelimiter { get; set; } = CsvDelimiter.Comma;
    public bool ParseDates { get; set; } = true;
    /// <summary>
    /// CSV only: prefix text that starts with = + - @ (or a tab/CR) with an apostrophe so spreadsheet programs
    /// can't run it as a formula. Plain numbers such as "-5.2" are left alone.
    /// </summary>
    public bool ProtectCsvFormulas { get; set; } = true;
    public string ArrayJoinSeparator { get; set; } = "; ";
    /// <summary>Data rows per worksheet (Excel limit is 1,048,576 including the header row).</summary>
    public int MaxRowsPerSheet { get; set; } = 1_048_575;
    /// <summary>Safety cap for the cartesian product when several arrays are expanded in one record.</summary>
    public int MaxExpandedRowsPerRecord { get; set; } = 5000;

    public char DelimiterChar => CsvDelimiter switch
    {
        CsvDelimiter.Semicolon => ';',
        CsvDelimiter.Tab => '\t',
        _ => ','
    };

    public ConversionSettings Clone()
    {
        var c = (ConversionSettings)MemberwiseClone();
        c.CustomColumnOrder = new List<string>(CustomColumnOrder);
        return c;
    }
}

public enum CellKind : byte { Missing, Empty, Null, Text, Number, Bool, DateTime }

/// <summary>A single typed cell. Text always holds the original textual form so CSV and Excel stay equivalent.</summary>
public readonly struct CellValue
{
    public CellKind Kind { get; }
    public string? Text { get; }
    public double Number { get; }
    public bool Bool { get; }
    public DateTime Date { get; }

    private CellValue(CellKind kind, string? text = null, double number = 0, bool b = false, DateTime date = default)
    {
        Kind = kind; Text = text; Number = number; Bool = b; Date = date;
    }

    public static CellValue Empty => new(CellKind.Empty);
    public static CellValue Null => new(CellKind.Null);
    public static CellValue FromText(string s) => new(CellKind.Text, s);
    public static CellValue FromNumber(string raw, double value) => new(CellKind.Number, raw, value);
    public static CellValue FromBool(bool b) => new(CellKind.Bool, b ? "true" : "false", 0, b);
    public static CellValue FromDate(string raw, DateTime d) => new(CellKind.DateTime, raw, 0, false, d);

    public string DisplayText(NullHandling nulls) => Kind switch
    {
        CellKind.Null => nulls == NullHandling.LiteralNull ? "null" : "",
        CellKind.Missing or CellKind.Empty => "",
        _ => Text ?? ""
    };
}

public sealed class JsonColumn
{
    public int Id { get; init; }
    /// <summary>Unique display name (original dotted path, de-duplicated if it collided).</summary>
    public string Name { get; init; } = "";
    /// <summary>Original dotted path before any collision renaming.</summary>
    public string OriginalPath { get; init; } = "";
    public string Identity { get; init; } = "";
    public int Depth { get; init; }
    public bool IsSystem { get; init; }
    public bool WasRenamed => !string.Equals(Name, OriginalPath, StringComparison.Ordinal);
}

public enum DataSourceKind { RootArray, ArrayProperty, RootObject }

public sealed record DataSourceCandidate(DataSourceKind Kind, IReadOnlyList<string> PathSegments, long Count, bool ContainsObjects)
{
    public string Display => Kind switch
    {
        DataSourceKind.RootArray => $"$ (root array) · {Count:N0} items",
        DataSourceKind.RootObject => "$ (whole object as one record)",
        _ => $"$.{string.Join(".", PathSegments)} · {Count:N0} items"
    };
    public string Path => Kind == DataSourceKind.ArrayProperty ? string.Join(".", PathSegments) : "";
}

public sealed class JsonStructureInfo
{
    public bool IsEmpty { get; init; }
    public bool IsUnsupportedRoot { get; init; }
    public IReadOnlyList<DataSourceCandidate> Candidates { get; init; } = Array.Empty<DataSourceCandidate>();
    /// <summary>True when more than one array of objects exists, so the user must choose.</summary>
    public bool RequiresSelection { get; init; }
    public DataSourceCandidate? Suggested { get; init; }
    public string RootKind { get; init; } = "";
}

public sealed class ConversionProgress
{
    public string Phase { get; init; } = "";
    public long Records { get; init; }
    public double Fraction { get; init; }
}

public sealed class ConversionResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public string Message { get; set; } = "";
    public string? TechnicalDetail { get; set; }
    public List<string> OutputFiles { get; } = new();
    public List<string> Warnings { get; } = new();
    public long RecordsProcessed { get; set; }
    public long RowsWritten { get; set; }
    public int ColumnCount { get; set; }
    public int TableCount { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Exception whose message is safe to show to end users.</summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class WarningCollector
{
    private const int Max = 200;
    private int _suppressed;
    public List<string> Items { get; } = new();
    public void Add(string w)
    {
        if (Items.Count < Max) Items.Add(w); else _suppressed++;
    }
    public IEnumerable<string> All() =>
        _suppressed > 0 ? Items.Append($"…and {_suppressed:N0} more warnings.") : Items;
}
