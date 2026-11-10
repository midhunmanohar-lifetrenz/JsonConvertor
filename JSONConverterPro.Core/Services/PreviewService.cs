using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

public sealed class PreviewResult
{
    public IReadOnlyList<string> ColumnNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string[]> Rows { get; init; } = Array.Empty<string[]>();
    public long TotalRecords { get; init; }
    public int TotalColumns { get; init; }
    public IReadOnlyList<string> ChildTables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Every detected column (including excluded ones) with type, fill rate and a sample: the "extracted structure".</summary>
    public IReadOnlyList<ColumnInfo> Structure { get; init; } = Array.Empty<ColumnInfo>();
}

/// <summary>Builds the on-screen preview with exactly the same flattening, ordering and column rules used for export.</summary>
public sealed class PreviewService
{
    private readonly ConversionService _conv;
    public PreviewService(ConversionService conv) { _conv = conv; }

    private static string TypeOf(ColumnStat? st)
    {
        if (st == null || st.Kinds == 0) return "empty";
        int k = st.Kinds;
        bool Only(CellKind kind) => k == (1 << (int)kind);
        if (Only(CellKind.Text)) return "text";
        if (Only(CellKind.Number)) return "number";
        if (Only(CellKind.Bool)) return "boolean";
        if (Only(CellKind.DateTime)) return "date";
        return "mixed";
    }

    public PreviewResult Build(string path, DataSourceCandidate source, ConversionSettings settings, int records,
        CancellationToken ct, Action<long, double>? onProgress = null)
    {
        var s = settings.Clone();
        var scan = _conv.Scan(path, source, s, records, ct, onProgress);
        var cols = scan.Tables.Get("").Columns;

        // Structure list: all columns, in the user's chosen order, regardless of include/exclude rules.
        var all = ColumnOrderingHelper.Order(cols, new ConversionSettings { ColumnOrdering = s.ColumnOrdering, CustomColumnOrder = s.CustomColumnOrder });
        var structure = all.Select(id =>
        {
            var st = id < scan.MainStats.Count ? scan.MainStats[id] : null;
            double pct = scan.MainRows > 0 && st != null ? Math.Round(100.0 * st.Filled / scan.MainRows, 1) : 0;
            return new ColumnInfo(cols[id].Name, cols[id].Depth, TypeOf(st), pct, st?.Sample ?? "", st?.Nulls ?? 0);
        }).ToList();

        var shape = ColumnShaper.Shape(cols, s);
        var rows = new List<string[]>();
        foreach (var (_, res) in scan.Sample)
            foreach (var r in res.Main)
            {
                var arr = new string[shape.Columns.Count];
                Array.Fill(arr, "");
                foreach (var kv in r.Cells)
                {
                    int p = shape.Pos[kv.Key];
                    if (p >= 0) arr[p] = ColumnShaper.Apply(kv.Value, shape.Masks[kv.Key]).DisplayText(s.NullHandling);
                }
                rows.Add(arr);
                if (rows.Count >= 1000) break;
            }
        return new PreviewResult
        {
            ColumnNames = shape.Columns.Select(c => c.Name).ToList(),
            Rows = rows,
            TotalRecords = scan.Records,
            TotalColumns = shape.Columns.Count,
            ChildTables = scan.Tables.Names.Where(n => n.Length > 0).ToList(),
            Warnings = scan.Warnings.All().ToList(),
            Structure = structure
        };
    }
}
