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
}

/// <summary>Builds the on-screen preview with exactly the same flattening and ordering used for export.</summary>
public sealed class PreviewService
{
    private readonly ConversionService _conv;
    public PreviewService(ConversionService conv) { _conv = conv; }

    public PreviewResult Build(string path, DataSourceCandidate source, ConversionSettings settings, int records,
        CancellationToken ct, Action<long, double>? onProgress = null)
    {
        var s = settings.Clone();
        var scan = _conv.Scan(path, source, s, records, ct, onProgress);
        var cols = scan.Tables.Get("").Columns;
        var order = ColumnOrderingHelper.Order(cols, s);
        var pos = new int[cols.Count];
        for (int i = 0; i < order.Length; i++) pos[order[i]] = i;

        var rows = new List<string[]>();
        foreach (var (_, res) in scan.Sample)
            foreach (var r in res.Main)
            {
                var arr = new string[order.Length];
                Array.Fill(arr, "");
                foreach (var kv in r.Cells) arr[pos[kv.Key]] = kv.Value.DisplayText(s.NullHandling);
                rows.Add(arr);
                if (rows.Count >= 200) break;
            }
        return new PreviewResult
        {
            ColumnNames = order.Select(id => cols[id].Name).ToList(),
            Rows = rows,
            TotalRecords = scan.Records,
            TotalColumns = order.Length,
            ChildTables = scan.Tables.Names.Where(n => n.Length > 0).ToList(),
            Warnings = scan.Warnings.All().ToList()
        };
    }
}
