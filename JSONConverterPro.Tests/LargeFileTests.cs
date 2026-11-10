using System.Diagnostics;
using System.Text;
using JSONConverterPro.Core.Models;
using Xunit;
using static JSONConverterPro.Tests.Harness;

namespace JSONConverterPro.Tests;

/// <summary>Memory is measured process-wide, so these tests must not share the process with other running tests.</summary>
[CollectionDefinition("Serial", DisableParallelization = true)]
public class SerialCollection { }

[Collection("Serial")]
public class LargeFileTests : IDisposable
{
    private readonly Harness h = new();
    public void Dispose() => h.Dispose();

    /// <summary>Writes a HIS-style wrapped JSON file of roughly the requested size without holding it in memory.</summary>
    private string Generate(string name, long targetBytes, out int count)
    {
        string path = Path.Combine(h.Dir, name);
        int n = 0;
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
        {
            w.Write("{\"status\":\"success\",\"data\":[");
            while (w.BaseStream.Length + 1 < targetBytes)
            {
                if (n > 0) w.Write(',');
                w.Write($"{{\"patientId\":{1000 + n},\"mrn\":\"{n:D8}\",\"patientName\":\"Patient {n} नमस्ते\",\"admitted\":\"2024-01-{1 + n % 28:D2}T08:30:00\"," +
                        $"\"demographics\":{{\"age\":{20 + n % 60},\"gender\":\"{(n % 2 == 0 ? "Male" : "Female")}\",\"address\":{{\"city\":\"Kochi\",\"pin\":\"68200{n % 10}\"}}}}," +
                        $"\"visits\":[{{\"visitId\":\"V{n}-1\",\"visitType\":\"OP\"}},{{\"visitId\":\"V{n}-2\",\"visitType\":\"IP\"}}],\"notes\":null,\"flags\":[true,false]}}");
                n++;
                if (n % 500 == 0) w.Flush();
            }
            w.Write("]}");
        }
        count = n;
        return path;
    }

    [Fact]
    public void TenMegabyteFile_ConvertsToCsv_AndExcel_WithMatchingData()
    {
        var p = Generate("10mb.json", 10 * 1024 * 1024, out int n);
        Assert.True(new FileInfo(p).Length >= 10 * 1024 * 1024);

        var sw = Stopwatch.StartNew();
        var csv = h.Run(p, Csv());
        Assert.True(csv.Success, csv.Message);
        Assert.Equal(n, csv.RecordsProcessed);

        var xlsx = h.Run(p, Xlsx());
        Assert.True(xlsx.Success, xlsx.Message);
        Assert.Equal(n, xlsx.RecordsProcessed);
        sw.Stop();

        var rows = ReadCsv(csv.OutputFiles[0]);
        Assert.Equal(n + 1, rows.Count);
        using var wb = OpenXlsx(xlsx.OutputFiles[0]);
        var ws = wb.Worksheet(1);
        Assert.Equal(n + 1, ws.LastRowUsed()!.RowNumber());
        Assert.Equal(rows[0].Count, ws.LastColumnUsed()!.ColumnNumber());

        // Equivalent logical data: spot-check first, middle and last rows
        foreach (int r in new[] { 1, n / 2, n })
            for (int c = 0; c < rows[0].Count; c++)
            {
                string csvText = rows[r][c];
                var cell = ws.Cell(r + 1, c + 1);
                if (cell.DataType == ClosedXML.Excel.XLDataType.DateTime) continue;
                if (cell.DataType == ClosedXML.Excel.XLDataType.Boolean) { Assert.Equal(csvText, cell.GetBoolean() ? "true" : "false"); continue; }
                if (cell.DataType == ClosedXML.Excel.XLDataType.Number) { Assert.Equal(double.Parse(csvText), cell.GetDouble()); continue; }
                Assert.Equal(csvText, cell.GetString());
            }
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void HundredMegabyteFile_StreamsToCsv_WithBoundedMemory()
    {
        var p = Generate("100mb.json", 100L * 1024 * 1024, out int n);
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        long peak = 0;
        var timer = new Timer(_ => peak = Math.Max(peak, GC.GetTotalMemory(false)), null, 0, 50);
        var r = h.Run(p, Csv());
        timer.Dispose();
        Assert.True(r.Success, r.Message);
        Assert.Equal(n, r.RecordsProcessed);
        long grew = peak - before;
        // The whole file is >100 MB; streaming must stay far below that.
        Assert.True(grew < 80L * 1024 * 1024, $"managed heap grew by {grew / 1024 / 1024} MB");
        Assert.Equal(n + 1, File.ReadLines(r.OutputFiles[0]).Count(l => l.Length > 0) );
    }

    [Fact]
    public void Streaming_HandlesRecordsLargerThanTheReadBuffer()
    {
        string big = new string('x', 700_000);   // > 256 KB initial buffer
        var p = h.Write("bigrec.json", $"[{{\"id\":1,\"blob\":\"{big}\"}},{{\"id\":2,\"blob\":\"y\"}}]");
        var r = h.Run(p, Csv());
        Assert.True(r.Success, r.Message);
        var rows = ReadCsv(r.OutputFiles[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(700_000, rows[1][1].Length);
    }
}
