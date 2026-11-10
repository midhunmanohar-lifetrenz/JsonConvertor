using System.Text.Json;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

/// <summary>
/// Assigns stable column ids. Columns are keyed by their structural identity, not their display name, so
/// two different JSON paths that flatten to the same text (e.g. {"a.b":1} and {"a":{"b":2}}) never overwrite each other.
/// </summary>
public sealed class ColumnRegistry
{
    private readonly Dictionary<string, int> _byIdentity = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    public List<JsonColumn> Columns { get; } = new();
    public int RenamedCount { get; private set; }

    public int Resolve(string identity, string name, int depth, bool isSystem = false)
    {
        if (_byIdentity.TryGetValue(identity, out int id)) return id;
        string unique = name;
        int n = 2;
        while (!_usedNames.Add(unique)) unique = $"{name} ({n++})";
        if (!ReferenceEquals(unique, name) && unique != name) RenamedCount++;
        id = Columns.Count;
        Columns.Add(new JsonColumn { Id = id, Name = unique, OriginalPath = name, Identity = identity, Depth = depth, IsSystem = isSystem });
        _byIdentity[identity] = id;
        return id;
    }
}

public sealed class TableRegistry
{
    private readonly Dictionary<string, ColumnRegistry> _tables = new(StringComparer.Ordinal);
    public List<string> Names { get; } = new();

    public TableRegistry() { Get(""); }

    public ColumnRegistry Get(string table)
    {
        if (!_tables.TryGetValue(table, out var r))
        {
            r = new ColumnRegistry();
            _tables[table] = r;
            Names.Add(table);
        }
        return r;
    }
}

public sealed class FlatRow
{
    public List<KeyValuePair<int, CellValue>> Cells { get; } = new();
    public void Add(int col, CellValue v) => Cells.Add(new(col, v));
    public FlatRow Clone() { var r = new FlatRow(); r.Cells.AddRange(Cells); return r; }
}

public sealed class FlatResult
{
    public List<FlatRow> Main { get; } = new();
    public Dictionary<string, List<FlatRow>> Children { get; } = new(StringComparer.Ordinal);
    public void AddChild(string table, FlatRow row)
    {
        if (!Children.TryGetValue(table, out var l)) Children[table] = l = new List<FlatRow>();
        l.Add(row);
    }
}

public sealed class FlattenService
{
    private const char IdSep = '\u001F';
    private readonly ConversionSettings _s;
    private readonly TableRegistry _tables;
    private readonly WarningCollector _warnings;

    private sealed class Ctx
    {
        public required string Table { get; init; }
        public required ColumnRegistry Reg { get; init; }
        public required FlatResult Res { get; init; }
        public long RecordNo { get; init; }
        public int ItemNo { get; init; }
        public bool Expand { get; set; }
        public bool ForceJoin { get; set; }
    }

    private sealed class ExpansionLimitException : Exception { }

    public FlattenService(ConversionSettings settings, TableRegistry tables, WarningCollector warnings)
    {
        _s = settings; _tables = tables; _warnings = warnings;
    }

    public FlatResult Flatten(JsonElement record, long recordNo)
    {
        bool expand = _s.ArrayHandling == ArrayHandling.ExpandRows;
        try
        {
            return Build(record, recordNo, expand);
        }
        catch (ExpansionLimitException)
        {
            _warnings.Add($"Record {recordNo}: expanding arrays would create more than {_s.MaxExpandedRowsPerRecord:N0} rows, so its arrays were combined into single cells instead.");
            return Build(record, recordNo, false);
        }
    }

    private FlatResult Build(JsonElement record, long recordNo, bool expand)
    {
        var res = new FlatResult();
        var main = _tables.Get("");
        var ctx = new Ctx { Table = "", Reg = main, Res = res, RecordNo = recordNo, Expand = expand };
        var rows = record.ValueKind == JsonValueKind.Object
            ? FlattenObject(record, "", "", ctx, 0)
            : FlattenValue(record, "value", "value", ctx, 0);

        if (_s.ArrayHandling == ArrayHandling.SeparateSheets)
        {
            int rn = main.Resolve("\u0002recordNo", "_recordNo", 0, true);
            foreach (var r in rows) r.Add(rn, CellValue.FromNumber(recordNo.ToString(), recordNo));
        }
        res.Main.AddRange(rows);
        return res;
    }

    private List<FlatRow> FlattenObject(JsonElement obj, string id, string name, Ctx c, int depth)
    {
        var rows = new List<FlatRow> { new FlatRow() };
        Dictionary<string, int>? seen = null;
        bool any = false;
        foreach (var p in obj.EnumerateObject())
        {
            any = true;
            seen ??= new Dictionary<string, int>(StringComparer.Ordinal);
            seen.TryGetValue(p.Name, out int k);
            seen[p.Name] = ++k;
            // Duplicate property names inside one object get their own identity (and a "(2)" display name), never overwritten.
            string seg = k == 1 ? p.Name : $"{p.Name}\u0001{k}";
            string childId = id.Length == 0 ? seg : id + IdSep + seg;
            string childName = name.Length == 0 ? p.Name : name + "." + p.Name;
            if (k > 1) _warnings.Add($"Duplicate property \"{childName}\" found; the extra value was kept in its own column.");

            var v = p.Value;
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var sub = FlattenValue(v, childId, childName, c, depth + 1);
                rows = Cross(rows, sub);
            }
            else
            {
                int col = c.Reg.Resolve(childId, childName, depth + 1);
                var cell = ScalarCell(v);
                foreach (var r in rows) r.Add(col, cell);
            }
        }
        if (!any && name.Length > 0)
        {
            // Empty object: keep the column so the property is never silently dropped.
            rows[0].Add(c.Reg.Resolve(id, name, depth), CellValue.Empty);
        }
        return rows;
    }

    private List<FlatRow> FlattenValue(JsonElement v, string id, string name, Ctx c, int depth)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                if (!_s.FlattenNestedObjects && depth > 0)
                    return Single(c.Reg.Resolve(id, name, depth), CellValue.FromText(JsonSerializer.Serialize(v)));
                return FlattenObject(v, id, name, c, depth);
            case JsonValueKind.Array:
                return HandleArray(v, id, name, c, depth);
            default:
                return Single(c.Reg.Resolve(id, name, depth), ScalarCell(v));
        }
    }

    private List<FlatRow> HandleArray(JsonElement arr, string id, string name, Ctx c, int depth)
    {
        int len = arr.GetArrayLength();
        // The array's own column is only created when the array itself is written to a cell. Arrays of objects
        // contribute per-field columns instead, so registering it eagerly would leave a stray empty column.
        int Col() => c.Reg.Resolve(id, name, depth);
        if (len == 0) return Single(Col(), CellValue.Empty);

        bool allPrim = true, allObj = true;
        foreach (var el in arr.EnumerateArray())
        {
            bool prim = el.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);
            if (!prim) allPrim = false;
            if (el.ValueKind != JsonValueKind.Object) allObj = false;
        }

        var mode = c.ForceJoin ? ArrayHandling.Join : _s.ArrayHandling;
        if (mode == ArrayHandling.JsonText)
            return Single(Col(), CellValue.FromText(JsonSerializer.Serialize(arr)));
        if (mode == ArrayHandling.SeparateSheets)
        {
            if (allObj) return SeparateSheet(arr, name, Col(), len, c);
            mode = ArrayHandling.Join;
        }
        if (mode == ArrayHandling.ExpandRows)
        {
            if (c.Expand) return ExpandArray(arr, id, name, c, depth);
            mode = ArrayHandling.Join;
        }

        // Join
        if (allPrim)
        {
            if (len == 1) return Single(Col(), ScalarCell(arr[0]));
            var parts = new List<string>(len);
            foreach (var el in arr.EnumerateArray()) parts.Add(Token(ScalarCell(el)));
            return Single(Col(), CellValue.FromText(string.Join(_s.ArrayJoinSeparator, parts)));
        }
        return JoinPerField(arr, id, name, len, c, depth);
    }

    private List<FlatRow> JoinPerField(JsonElement arr, string id, string name, int len, Ctx c, int depth)
    {
        bool prev = c.ForceJoin;
        c.ForceJoin = true;
        var tokens = new Dictionary<int, string[]>();
        var order = new List<int>();
        int i = 0;
        try
        {
            foreach (var el in arr.EnumerateArray())
            {
                var rows = FlattenValue(el, id, name, c, depth);
                if (len == 1) return rows; // a single element keeps its original cell types
                foreach (var kv in rows[0].Cells)
                {
                    if (!tokens.TryGetValue(kv.Key, out var arrTokens))
                    {
                        arrTokens = new string[len];
                        Array.Fill(arrTokens, "");
                        tokens[kv.Key] = arrTokens;
                        order.Add(kv.Key);
                    }
                    arrTokens[i] = Token(kv.Value);
                }
                i++;
            }
        }
        finally { c.ForceJoin = prev; }

        var row = new FlatRow();
        foreach (int col in order)
        {
            var t = tokens[col];
            // A single element keeps its original type; multiple elements are joined as text.
            row.Add(col, CellValue.FromText(string.Join(_s.ArrayJoinSeparator, t)));
        }
        if (row.Cells.Count == 0) row.Add(c.Reg.Resolve(id, name, depth), CellValue.Empty);
        return new List<FlatRow> { row };
    }

    private List<FlatRow> ExpandArray(JsonElement arr, string id, string name, Ctx c, int depth)
    {
        var result = new List<FlatRow>();
        foreach (var el in arr.EnumerateArray())
        {
            result.AddRange(FlattenValue(el, id, name, c, depth));
            if (result.Count > _s.MaxExpandedRowsPerRecord) throw new ExpansionLimitException();
        }
        return result;
    }

    private List<FlatRow> SeparateSheet(JsonElement arr, string relName, int mainCol, int len, Ctx c)
    {
        string table = c.Table.Length == 0 ? relName : c.Table + "." + relName;
        var reg = _tables.Get(table);
        int rn = reg.Resolve("\u0002recordNo", "_recordNo", 0, true);
        int pn = reg.Resolve("\u0002parentItemNo", "_parentItemNo", 0, true);
        int ino = reg.Resolve("\u0002itemNo", "_itemNo", 0, true);
        int i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            i++;
            var child = new Ctx { Table = table, Reg = reg, Res = c.Res, RecordNo = c.RecordNo, ItemNo = i, Expand = false };
            var rows = FlattenObject(el, "", "", child, 0);
            var row = rows[0];
            row.Add(rn, CellValue.FromNumber(c.RecordNo.ToString(), c.RecordNo));
            if (c.Table.Length > 0) row.Add(pn, CellValue.FromNumber(c.ItemNo.ToString(), c.ItemNo));
            row.Add(ino, CellValue.FromNumber(i.ToString(), i));
            c.Res.AddChild(table, row);
        }
        return Single(mainCol, CellValue.FromText($"→ see '{table}' ({len} rows)"));
    }

    private List<FlatRow> Cross(List<FlatRow> a, List<FlatRow> b)
    {
        if (b.Count == 1)
        {
            var cells = b[0].Cells;
            foreach (var r in a) r.Cells.AddRange(cells);
            return a;
        }
        if (b.Count == 0) return a;
        if ((long)a.Count * b.Count > _s.MaxExpandedRowsPerRecord) throw new ExpansionLimitException();
        var res = new List<FlatRow>(a.Count * b.Count);
        foreach (var x in a)
            foreach (var y in b)
            {
                var n = x.Clone();
                n.Cells.AddRange(y.Cells);
                res.Add(n);
            }
        return res;
    }

    private static List<FlatRow> Single(int col, CellValue v)
    {
        var r = new FlatRow();
        r.Add(col, v);
        return new List<FlatRow> { r };
    }

    private string Token(CellValue v) => v.DisplayText(_s.NullHandling);

    private CellValue ScalarCell(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
            {
                string s = v.GetString() ?? "";
                if (_s.ParseDates && DateFormatHelper.TryParseReliable(s, out var d)) return CellValue.FromDate(s, d);
                return CellValue.FromText(s);
            }
            case JsonValueKind.Number:
            {
                string raw = v.GetRawText();
                double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dbl);
                return CellValue.FromNumber(raw, dbl);
            }
            case JsonValueKind.True: return CellValue.FromBool(true);
            case JsonValueKind.False: return CellValue.FromBool(false);
            case JsonValueKind.Null: return CellValue.Null;
            default: return CellValue.Empty;
        }
    }
}
