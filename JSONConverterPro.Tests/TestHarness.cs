using System.Text;
using ClosedXML.Excel;
using JSONConverterPro.Core.Models;
using JSONConverterPro.Core.Services;

namespace JSONConverterPro.Tests;

public sealed class Harness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "jcp-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public FileService Files { get; } = new();
    public JsonParserService Parser { get; } = new();
    public JsonSchemaService Schema { get; } = new();
    public ConversionService Conv { get; }
    public PreviewService Preview { get; }

    public Harness()
    {
        Directory.CreateDirectory(Dir);
        Conv = new ConversionService(Parser, Files, new CsvExportService(), new ExcelExportService());
        Preview = new PreviewService(Conv);
    }

    public string Write(string name, string content, Encoding? enc = null)
    {
        string p = Path.Combine(Dir, name);
        File.WriteAllText(p, content, enc ?? new UTF8Encoding(false));
        return p;
    }

    public JsonStructureInfo Analyze(string path)
    {
        using var s = Files.OpenRead(path);
        return Schema.Analyze(s, CancellationToken.None);
    }

    public ConversionResult Run(string path, ConversionSettings? settings = null, DataSourceCandidate? source = null,
        Func<IReadOnlyList<string>, bool>? confirm = null, CancellationToken ct = default, string? outDir = null)
    {
        var info = Analyze(path);
        var src = source ?? info.Suggested ?? throw new InvalidOperationException("no source");
        var res = Conv.Convert(new ConversionRequest
        {
            InputPath = path,
            Source = src,
            Settings = settings ?? new ConversionSettings(),
            OutputDirectory = outDir ?? Path.Combine(Dir, "out"),
            ConfirmOverwrite = confirm ?? (_ => true)
        }, null, ct);
        if (!res.Success && res.Message.StartsWith("The conversion failed unexpectedly"))
            throw new InvalidOperationException(res.TechnicalDetail);
        return res;
    }

    public static ConversionSettings Csv(Action<ConversionSettings>? tweak = null)
    {
        var s = new ConversionSettings { OutputFormat = OutputFormat.Csv };
        tweak?.Invoke(s);
        return s;
    }

    public static ConversionSettings Xlsx(Action<ConversionSettings>? tweak = null)
    {
        var s = new ConversionSettings { OutputFormat = OutputFormat.Excel };
        tweak?.Invoke(s);
        return s;
    }

    /// <summary>RFC 4180 parser (handles quotes, delimiters and multi-line values) that skips a UTF-8 BOM.</summary>
    public static List<List<string>> ReadCsv(string path, char delim = ',')
    {
        string text = File.ReadAllText(path, new UTF8Encoding(false));
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == delim) { row.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(sb.ToString()); sb.Clear(); rows.Add(row); row = new List<string>(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    public static XLWorkbook OpenXlsx(string path) => new(new MemoryStream(File.ReadAllBytes(path)));

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}
