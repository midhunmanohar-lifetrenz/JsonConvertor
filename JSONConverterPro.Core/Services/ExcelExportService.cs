using ClosedXML.Excel;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

public sealed class ExcelExportService
{
    public const int ExcelMaxColumns = 16384;
    public const int ExcelMaxCellChars = 32767;

    public IExportWriter CreateWriter(IReadOnlyList<TableSchema> tables, string path, ConversionSettings settings, WarningCollector warnings)
        => new ExcelWriter(tables, path, settings, warnings);

    private sealed class TableState
    {
        public required TableSchema Schema { get; init; }
        public IXLWorksheet? Sheet;
        public int Row;            // last written row on the current sheet (1 = header only)
        public int SheetNo;
        public int[] Widths = Array.Empty<int>();
        public int Sampled;
    }

    private sealed class ExcelWriter : IExportWriter
    {
        private readonly XLWorkbook _wb = new();
        private readonly TableState[] _t;
        private readonly string _path;
        private readonly ConversionSettings _s;
        private readonly WarningCollector _warn;
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private bool _truncWarned;

        public ExcelWriter(IReadOnlyList<TableSchema> tables, string path, ConversionSettings s, WarningCollector warn)
        {
            _path = path; _s = s; _warn = warn;
            _t = tables.Select(x => new TableState { Schema = x }).ToArray();
            foreach (var t in _t) StartSheet(t);
        }

        private string UniqueSheetName(string baseName, int sheetNo)
        {
            string clean = new string(baseName.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim('\'', ' ');
            if (clean.Length == 0) clean = "Sheet";
            string suffix = sheetNo > 1 ? $" ({sheetNo})" : "";
            string name = clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] + suffix : clean + suffix;
            int n = sheetNo;
            while (!_names.Add(name))
            {
                n++;
                suffix = $" ({n})";
                name = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
            }
            return name;
        }

        private void StartSheet(TableState t)
        {
            if (t.Sheet != null) FinishSheet(t);
            t.SheetNo++;
            string baseName = t.Schema.Name.Length == 0 ? (string.IsNullOrWhiteSpace(_s.SheetName) ? "Data" : _s.SheetName) : t.Schema.Name;
            t.Sheet = _wb.Worksheets.Add(UniqueSheetName(baseName, t.SheetNo));
            var cols = t.Schema.Columns;
            t.Widths = new int[cols.Count];
            for (int c = 0; c < cols.Count; c++)
            {
                t.Sheet.Cell(1, c + 1).Value = Sanitize(cols[c].Name);
                t.Widths[c] = cols[c].Name.Length;
            }
            t.Row = 1;
            t.Sampled = 0;
        }

        private void FinishSheet(TableState t)
        {
            var ws = t.Sheet!;
            int n = t.Schema.Columns.Count;
            if (n == 0) return;
            ws.Range(1, 1, 1, n).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            ws.Range(1, 1, Math.Max(t.Row, 1), n).SetAutoFilter();
            for (int c = 0; c < n; c++)
                ws.Column(c + 1).Width = Math.Clamp(t.Widths[c] * 1.1 + 2, 8, 60);
        }

        public void WriteRow(int ti, CellValue[] cells)
        {
            var t = _t[ti];
            if (t.Row - 1 >= _s.MaxRowsPerSheet) StartSheet(t);
            int r = ++t.Row;
            var ws = t.Sheet!;
            bool sample = t.Sampled < 2000;
            if (sample) t.Sampled++;

            for (int c = 0; c < cells.Length; c++)
            {
                var v = cells[c];
                int len = 0;
                switch (v.Kind)
                {
                    case CellKind.Missing:
                    case CellKind.Empty:
                        continue;
                    case CellKind.Null:
                        if (_s.NullHandling == NullHandling.LiteralNull) { ws.Cell(r, c + 1).Value = "null"; len = 4; }
                        break;
                    case CellKind.Text:
                    {
                        string s = Sanitize(v.Text ?? "");
                        ws.Cell(r, c + 1).Value = s; len = s.Length;
                        break;
                    }
                    case CellKind.Number:
                    {
                        if (NumberHelper.CanBeExcelNumber(v.Text ?? "", out double d))
                        {
                            var cell = ws.Cell(r, c + 1);
                            cell.Value = d;
                            // Excel's General format switches to scientific notation past ~11 digits.
                            if (Math.Abs(d) >= 1e11 && d == Math.Floor(d)) cell.Style.NumberFormat.Format = "0";
                        }
                        else ws.Cell(r, c + 1).Value = v.Text ?? ""; // keep every digit of long IDs
                        len = (v.Text ?? "").Length;
                        break;
                    }
                    case CellKind.Bool:
                        ws.Cell(r, c + 1).Value = v.Bool; len = 5;
                        break;
                    case CellKind.DateTime:
                    {
                        var cell = ws.Cell(r, c + 1);
                        cell.Value = v.Date;
                        cell.Style.DateFormat.Format = DateFormatHelper.ExcelFormat(v.Date);
                        len = 19;
                        break;
                    }
                }
                if (sample && len > t.Widths[c]) t.Widths[c] = Math.Min(len, 60);
            }
        }

        private string Sanitize(string s)
        {
            bool changed = false;
            if (s.Length > ExcelMaxCellChars)
            {
                s = s[..ExcelMaxCellChars]; changed = true;
                if (!_truncWarned) { _warn.Add("Some cell values exceed Excel's 32,767-character limit and were truncated. Use CSV to keep them in full."); _truncWarned = true; }
            }
            // Control characters that are illegal in XLSX XML
            bool bad = false;
            foreach (char ch in s) if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') { bad = true; break; }
            if (bad)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (char ch in s) sb.Append(ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' ? '�' : ch);
                s = sb.ToString();
                _warn.Add("Some values contained control characters that Excel can't store; they were replaced with �.");
            }
            _ = changed;
            return s;
        }

        public void Complete()
        {
            foreach (var t in _t) if (t.Sheet != null) FinishSheet(t);
            using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            _wb.SaveAs(fs);
        }

        public void Dispose() => _wb.Dispose();
    }
}
