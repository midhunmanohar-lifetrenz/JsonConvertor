using System.Diagnostics;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;
using Microsoft.Extensions.Logging;

namespace JSONConverterPro.Core.Services;

public sealed class ConversionRequest
{
    public required string InputPath { get; init; }
    public required DataSourceCandidate Source { get; init; }
    public required ConversionSettings Settings { get; init; }
    public required string OutputDirectory { get; init; }
    /// <summary>Called (on the worker thread) with the files that already exist; return true to overwrite them.</summary>
    public Func<IReadOnlyList<string>, bool>? ConfirmOverwrite { get; init; }
    /// <summary>Override the output base name (defaults to the input file name without extension).</summary>
    public string? OutputBaseName { get; init; }
}

public sealed class ScanOutcome
{
    public long Records { get; set; }
    public TableRegistry Tables { get; init; } = new();
    public WarningCollector Warnings { get; init; } = new();
    public List<(long RecordNo, FlatResult Result)> Sample { get; } = new();
    public long MainRows { get; set; }
    public List<ColumnStat> MainStats { get; } = new();
}

public sealed class ColumnStat
{
    public long Filled, Nulls;
    public int Kinds;
    public string? Sample;
}

/// <summary>Orchestrates: scan (learn all columns) → order columns → write rows. Streams both passes.</summary>
public sealed class ConversionService
{
    private readonly JsonParserService _parser;
    private readonly FileService _files;
    private readonly CsvExportService _csv;
    private readonly ExcelExportService _excel;
    private readonly ILogger<ConversionService>? _log;

    public ConversionService(JsonParserService parser, FileService files, CsvExportService csv, ExcelExportService excel, ILogger<ConversionService>? log = null)
    {
        _parser = parser; _files = files; _csv = csv; _excel = excel; _log = log;
    }

    /// <summary>First pass: reads every record to discover the full column set. Optionally keeps a small sample for preview.</summary>
    public ScanOutcome Scan(string path, DataSourceCandidate source, ConversionSettings settings, int sampleRecords,
        CancellationToken ct, Action<long, double>? onProgress = null)
    {
        var outcome = new ScanOutcome();
        var flatten = new FlattenService(settings, outcome.Tables, outcome.Warnings);
        long size = new FileInfo(path).Length;
        long lastBytes = 0;
        using var stream = _files.OpenRead(path);
        _parser.ForEachRecord(stream, source, (el, idx) =>
        {
            var res = flatten.Flatten(el, idx);
            outcome.Records = idx;
            foreach (var row in res.Main)
            {
                outcome.MainRows++;
                foreach (var kv in row.Cells)
                {
                    while (outcome.MainStats.Count <= kv.Key) outcome.MainStats.Add(new ColumnStat());
                    var st = outcome.MainStats[kv.Key];
                    switch (kv.Value.Kind)
                    {
                        case CellKind.Null: st.Nulls++; break;
                        case CellKind.Missing: case CellKind.Empty: break;
                        default:
                            st.Filled++; st.Kinds |= 1 << (int)kv.Value.Kind;
                            st.Sample ??= (kv.Value.Text ?? "") is { Length: > 60 } t ? t[..60] + "…" : kv.Value.Text ?? "";
                            break;
                    }
                }
            }
            if (idx <= sampleRecords) outcome.Sample.Add((idx, res));
            return true;
        }, ct, b =>
        {
            lastBytes = b;
            onProgress?.Invoke(outcome.Records, size > 0 ? Math.Min(1.0, (double)lastBytes / size) : 1);
        });
        foreach (var name in outcome.Tables.Names)
        {
            int renamed = outcome.Tables.Get(name).RenamedCount;
            if (renamed > 0)
                outcome.Warnings.Add($"{renamed} column name(s) collided after flattening and were made unique with a numeric suffix{(name.Length > 0 ? $" in '{name}'" : "")}.");
        }
        return outcome;
    }

    public ConversionResult Convert(ConversionRequest req, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ConversionResult();
        var temps = new List<string>();
        IExportWriter? writer = null;
        try
        {
            _files.ValidateInput(req.InputPath);
            var settings = req.Settings.Clone();
            _files.EnsureOutputDirectory(req.OutputDirectory);
            long inputSize = new FileInfo(req.InputPath).Length;
            _files.CheckDiskSpace(req.OutputDirectory, inputSize * (settings.OutputFormat == OutputFormat.Excel ? 2 : 1) + 1_000_000);

            // ---- Pass 1: scan
            progress?.Report(new ConversionProgress { Phase = "Scanning structure…", Fraction = 0 });
            var scan = _ScanWithProgress(req, settings, progress, ct);
            result.RecordsProcessed = scan.Records;
            if (scan.Records == 0) throw new UserFacingException("The selected data array contains no records, so there is nothing to export.");

            // ---- Column order & schemas
            var tableNames = scan.Tables.Names.ToList();
            var schemas = new List<TableSchema>();
            var shapes = new List<TableShape>();
            foreach (var tn in tableNames)
            {
                var shape = ColumnShaper.Shape(scan.Tables.Get(tn).Columns, settings);
                // Only the main table must keep a column. A linked table left with just its link columns is still valid.
                if (tn.Length == 0 && shape.DataColumnCount == 0)
                    throw new UserFacingException("Every column is excluded, so there is nothing to export. Include at least one column.");
                shapes.Add(shape);
                schemas.Add(new TableSchema(tn, shape.Columns));
            }
            if (settings.OutputFormat == OutputFormat.Excel && schemas.Any(s => s.Columns.Count > ExcelExportService.ExcelMaxColumns))
                throw new UserFacingException($"This data has more than {ExcelExportService.ExcelMaxColumns:N0} columns, which is Excel's limit. Choose CSV, or use array handling that creates fewer columns.");
            result.ColumnCount = schemas[0].Columns.Count;
            result.TableCount = schemas.Count;

            // ---- Output plan & overwrite check
            string baseName = FileService.SanitizeFileName(req.OutputBaseName ?? Path.GetFileNameWithoutExtension(req.InputPath));
            if (baseName.Length == 0) baseName = "output";
            var finals = new List<string>();
            if (settings.OutputFormat == OutputFormat.Excel)
                finals.Add(Path.Combine(req.OutputDirectory, baseName + ".xlsx"));
            else
            {
                // File names must differ even on file systems that ignore letter case ("Items" and "items").
                var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in schemas)
                {
                    string stem = s.Name.Length == 0 ? baseName : $"{baseName}_{FileService.SanitizeFileName(s.Name)}";
                    string unique = stem; int n = 2;
                    while (!usedFiles.Add(unique)) unique = $"{stem}_{n++}";
                    finals.Add(Path.Combine(req.OutputDirectory, unique + ".csv"));
                }
            }

            var existing = finals.Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                bool ok = req.ConfirmOverwrite?.Invoke(existing) ?? false;
                if (!ok)
                    throw new UserFacingException(existing.Count == 1
                        ? $"'{Path.GetFileName(existing[0])}' already exists. Choose another folder or allow overwriting."
                        : $"{existing.Count} output files already exist. Choose another folder or allow overwriting.");
            }

            // Write to temporary files and move into place at the end: a failure or cancel never leaves a half-written file.
            foreach (var f in finals) temps.Add(f + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            var excelWarnings = new WarningCollector();
            writer = settings.OutputFormat == OutputFormat.Excel
                ? _excel.CreateWriter(schemas, temps[0], settings, excelWarnings)
                : _csv.CreateWriter(schemas, temps, settings);

            // ---- Pass 2: write
            var flatten = new FlattenService(settings, scan.Tables, new WarningCollector());
            var tableIndex = tableNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
            var buffers = schemas.Select(s => new CellValue[s.Columns.Count]).ToArray();
            long size = inputSize, rows = 0;

            void Emit(int t, FlatRow row)
            {
                var buf = buffers[t];
                Array.Clear(buf);
                var shape = shapes[t];
                foreach (var kv in row.Cells)
                {
                    int p = shape.Pos[kv.Key];
                    if (p >= 0) buf[p] = ColumnShaper.Apply(kv.Value, shape.Masks[kv.Key]);
                }
                writer!.WriteRow(t, buf);
                if (t == 0) rows++;
            }

            using (var stream = _files.OpenRead(req.InputPath))
            {
                _parser.ForEachRecord(stream, req.Source, (el, idx) =>
                {
                    var res = flatten.Flatten(el, idx);
                    foreach (var r in res.Main) Emit(0, r);
                    foreach (var kv in res.Children)
                    {
                        int t = tableIndex[kv.Key];
                        foreach (var r in kv.Value) Emit(t, r);
                    }
                    return true;
                }, ct, b => progress?.Report(new ConversionProgress
                {
                    Phase = "Writing…",
                    Records = Math.Min(rows, scan.Records),
                    Fraction = 0.35 + 0.6 * (size > 0 ? Math.Min(1.0, (double)b / size) : 1)
                }));
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress { Phase = "Saving file…", Records = scan.Records, Fraction = 0.97 });
            writer.Complete();
            writer.Dispose();
            writer = null;

            for (int i = 0; i < finals.Count; i++) File.Move(temps[i], finals[i], overwrite: true);
            temps.Clear();

            result.OutputFiles.AddRange(finals);
            result.RowsWritten = rows;
            result.Warnings.AddRange(scan.Warnings.All());
            result.Warnings.AddRange(excelWarnings.All());
            result.Success = true;
            result.Message = $"Converted {scan.Records:N0} records ({rows:N0} rows) to {finals.Count} file{(finals.Count == 1 ? "" : "s")}.";
            progress?.Report(new ConversionProgress { Phase = "Done", Records = scan.Records, Fraction = 1 });
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Message = "Conversion was cancelled. No output file was kept.";
        }
        catch (Exception ex)
        {
            result.Message = _files.Describe(ex);
            result.TechnicalDetail = ex.ToString();
            _log?.LogError(ex, "Conversion failed for {File}", req.InputPath);
        }
        finally
        {
            try { writer?.Dispose(); } catch { /* ignore */ }
            foreach (var t in temps) FileService.TryDelete(t);
            sw.Stop();
            result.Elapsed = sw.Elapsed;
        }
        return result;
    }

    private ScanOutcome _ScanWithProgress(ConversionRequest req, ConversionSettings settings, IProgress<ConversionProgress>? progress, CancellationToken ct)
        => Scan(req.InputPath, req.Source, settings, 0, ct, (records, frac) =>
            progress?.Report(new ConversionProgress { Phase = "Scanning structure…", Records = records, Fraction = frac * 0.35 }));
}
