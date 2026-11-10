using System.Text;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

public sealed record TableSchema(string Name, IReadOnlyList<JsonColumn> Columns);

public interface IExportWriter : IDisposable
{
    /// <summary>Rows must already be in output column order.</summary>
    void WriteRow(int tableIndex, CellValue[] cells);
    void Complete();
}

/// <summary>Streams rows straight to disk, so memory use does not grow with the size of the output.</summary>
public sealed class CsvExportService
{
    public IExportWriter CreateWriter(IReadOnlyList<TableSchema> tables, IReadOnlyList<string> paths, ConversionSettings settings)
        => new CsvWriter(tables, paths, settings);

    internal static string Escape(string s, char delimiter)
    {
        if (s.Length == 0) return s;
        bool needs = s.IndexOf(delimiter) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0
                     || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]);
        return needs ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private sealed class CsvWriter : IExportWriter
    {
        private readonly StreamWriter[] _w;
        private readonly ConversionSettings _s;
        private readonly char _d;

        public CsvWriter(IReadOnlyList<TableSchema> tables, IReadOnlyList<string> paths, ConversionSettings s)
        {
            _s = s; _d = s.DelimiterChar;
            _w = new StreamWriter[tables.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                // UTF-8 with BOM so Excel opens non-ASCII text correctly.
                _w[i] = new StreamWriter(new FileStream(paths[i], FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024),
                    new UTF8Encoding(true), 64 * 1024) { NewLine = "\r\n" };
                var cols = tables[i].Columns;
                for (int c = 0; c < cols.Count; c++)
                {
                    if (c > 0) _w[i].Write(_d);
                    _w[i].Write(Escape(cols[c].Name, _d));
                }
                _w[i].Write("\r\n");
            }
        }

        public void WriteRow(int t, CellValue[] cells)
        {
            var w = _w[t];
            for (int c = 0; c < cells.Length; c++)
            {
                if (c > 0) w.Write(_d);
                string text = cells[c].DisplayText(_s.NullHandling);
                if (text.Length > 0) w.Write(Escape(text, _d));
            }
            w.Write("\r\n");
        }

        public void Complete()
        {
            foreach (var w in _w) w.Flush();
        }

        public void Dispose()
        {
            foreach (var w in _w) w?.Dispose();
        }
    }
}
