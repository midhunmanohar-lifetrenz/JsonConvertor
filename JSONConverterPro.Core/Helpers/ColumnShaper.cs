using System.Security.Cryptography;
using System.Text;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Helpers;

public enum MaskMode { None, Redact, Hash }

/// <summary>User rule for one flattened column: leave it out, rename its header, or mask its values (de-identification).</summary>
public sealed class ColumnRule
{
    public string Name { get; set; } = "";
    public bool Include { get; set; } = true;
    public string? RenameTo { get; set; }
    public MaskMode Mask { get; set; } = MaskMode.None;
    public bool IsDefault => Include && string.IsNullOrWhiteSpace(RenameTo) && Mask == MaskMode.None;
}

/// <summary>The final shape of one output table after ordering and user rules.</summary>
public sealed class TableShape
{
    public required IReadOnlyList<JsonColumn> Columns { get; init; }
    /// <summary>Source column id → output index, or -1 when the column is excluded.</summary>
    public required int[] Pos { get; init; }
    public required MaskMode[] Masks { get; init; }
    public int DataColumnCount => Columns.Count(c => !c.IsSystem);
}

public static class ColumnShaper
{
    public const string RedactedText = "[REDACTED]";
    // One random key per app session: masked values are consistent between preview and export, never across sessions.
    private static readonly byte[] SessionKey = RandomNumberGenerator.GetBytes(32);

    public static TableShape Shape(IReadOnlyList<JsonColumn> source, ConversionSettings settings)
    {
        var rules = settings.ColumnRules.Where(r => r.Name.Length > 0).GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var order = ColumnOrderingHelper.Order(source, settings);
        var pos = new int[source.Count];
        Array.Fill(pos, -1);
        var masks = new MaskMode[source.Count];
        var cols = new List<JsonColumn>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (int id in order)
        {
            var c = source[id];
            rules.TryGetValue(c.Name, out var r);
            if (!c.IsSystem && r is { Include: false }) continue;
            masks[id] = !c.IsSystem && r != null ? r.Mask : MaskMode.None;
            string header = !c.IsSystem && r != null && !string.IsNullOrWhiteSpace(r.RenameTo) ? r.RenameTo!.Trim() : c.Name;
            string unique = header; int n = 2;
            while (!used.Add(unique)) unique = $"{header} ({n++})";
            pos[id] = cols.Count;
            cols.Add(new JsonColumn { Id = c.Id, Name = unique, OriginalPath = c.OriginalPath, Identity = c.Identity, Depth = c.Depth, IsSystem = c.IsSystem });
        }
        return new TableShape { Columns = cols, Pos = pos, Masks = masks };
    }

    public static CellValue Apply(CellValue v, MaskMode mode)
    {
        if (mode == MaskMode.None) return v;
        if (v.Kind is CellKind.Missing or CellKind.Empty or CellKind.Null) return v;   // nothing to hide, keep missing/null distinct
        if (mode == MaskMode.Redact) return CellValue.FromText(RedactedText);
        var hash = HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes(v.Text ?? ""));
        return CellValue.FromText(Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
    }
}
