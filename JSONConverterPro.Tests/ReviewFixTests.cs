using ClosedXML.Excel;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;
using Xunit;
using static JSONConverterPro.Tests.Harness;

namespace JSONConverterPro.Tests;

/// <summary>Regression tests for the issues found in the 1.0.1 review.</summary>
public class ReviewFixTests : IDisposable
{
    private readonly Harness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public void LinkedCsvFiles_DifferingOnlyByCase_AreBothKept()
    {
        var p = h.Write("c.json", """[{"id":1,"Items":[{"a":"UPPER"}],"items":[{"b":"lower"}]}]""");
        var r = h.Run(p, Csv(s => s.ArrayHandling = ArrayHandling.SeparateSheets));
        Assert.True(r.Success, r.Message);
        Assert.Equal(3, r.OutputFiles.Count);
        Assert.Equal(3, r.OutputFiles.Select(f => f.ToLowerInvariant()).Distinct().Count());
        Assert.All(r.OutputFiles, f => Assert.True(File.Exists(f)));
        string all = string.Concat(r.OutputFiles.Skip(1).Select(File.ReadAllText));
        Assert.Contains("UPPER", all);
        Assert.Contains("lower", all);
    }

    [Fact]
    public void EmptyPropertyName_DoesNotOverwriteSibling()
    {
        var p = h.Write("e.json", """[{"":{"a":"INNER"},"a":"OUTER"}]""");
        var r = h.Run(p, Csv());
        var rows = ReadCsv(r.OutputFiles[0]);
        Assert.Equal(2, rows[0].Count);
        Assert.Contains("INNER", rows[1]);
        Assert.Contains("OUTER", rows[1]);
    }

    [Fact]
    public void ExpandRows_TwoLists_WarnsAboutCombinations()
    {
        var p = h.Write("x.json", """[{"id":1,"total":100,"a":[1,2,3],"b":["x","y"]},{"id":2,"total":5,"a":[1],"b":["z"]}]""");
        var r = h.Run(p, Csv(s => s.ArrayHandling = ArrayHandling.ExpandRows));
        Assert.Equal(7, r.RowsWritten);
        Assert.Single(r.Warnings, w => w.Contains("every combination"));
        // One list only: no warning.
        var p2 = h.Write("y.json", """[{"id":1,"a":[1,2,3]}]""");
        Assert.DoesNotContain(h.Run(p2, Csv(s => s.ArrayHandling = ArrayHandling.ExpandRows)).Warnings, w => w.Contains("every combination"));
    }

    [Theory]
    [InlineData("-", "-")]
    [InlineData("--", "--")]
    [InlineData("+91 98765 43210", "+91 98765 43210")]
    [InlineData("-5", "-5")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("-2 to 3", "'-2 to 3")]
    [InlineData("+91-98765", "'+91-98765")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("+SUM(A1:A9)", "'+SUM(A1:A9)")]
    [InlineData("@x", "'@x")]
    public void CsvProtection_LeavesDashesAndPhoneNumbersAlone(string value, string expected)
    {
        var p = h.Write("f.json", System.Text.Json.JsonSerializer.Serialize(new[] { new { v = value } }));
        var rows = ReadCsv(h.Run(p, Csv()).OutputFiles[0]);
        Assert.Equal(expected, rows[1][0]);
    }

    [Fact]
    public void DamagedCharacter_IsReplaced_NotFatal()
    {
        var p = h.Write("s.json", "[{\"a\":\"x\\ud83dy\",\"b\":\"ok \\ud83d\\ude00\"}]");
        foreach (var st in new[] { Csv(), Xlsx() })
        {
            var r = h.Run(p, st);
            Assert.True(r.Success, r.Message);
            Assert.Contains(r.Warnings, w => w.Contains("damaged characters"));
        }
        var rows = ReadCsv(Path.Combine(h.Dir, "out", "s.csv"));
        Assert.Equal("x�y", rows[1][0]);
        Assert.Equal("ok \U0001F600", rows[1][1]);
    }

    [Fact]
    public void JsonLines_WithVeryLongLines_IsDetected()
    {
        string big = new('x', 200_000);
        var p = h.Write("l.jsonl", $"{{\"id\":1,\"t\":\"{big}\"}}\n{{\"id\":2,\"t\":\"{big}\"}}\n{{\"id\":3,\"t\":\"s\"}}\n");
        var info = h.Analyze(p);
        Assert.Equal(3, info.Suggested!.Count);
        // A large single-line JSON document is still read as ordinary JSON.
        var one = h.Write("m.json", "[" + string.Join(",", Enumerable.Range(0, 30000).Select(i => $"{{\"id\":{i}}}")) + "]");
        Assert.Equal(30000, h.Analyze(one).Suggested!.Count);
    }

    [Fact]
    public void ExcludedColumn_DoesNotBlockExport_WhenLinkedTableHasOnlyThatColumn()
    {
        var p = h.Write("k.json", """[{"id":1,"name":"n","kids":[{"id":7}]}]""");
        var r = h.Run(p, Xlsx(s =>
        {
            s.ArrayHandling = ArrayHandling.SeparateSheets;
            s.ColumnRules.Add(new ColumnRule { Name = "id", Include = false });
        }));
        Assert.True(r.Success, r.Message);
        // Excluding everything in the main table is still an error.
        var all = h.Run(p, Csv(s => s.ColumnRules.AddRange(new[] { "id", "name", "kids.id" }.Select(n => new ColumnRule { Name = n, Include = false }))));
        Assert.False(all.Success);
    }

    [Fact]
    public void Excel_HistorySheet_Milliseconds_AndTrailingZeros()
    {
        var p = h.Write("x.json", """[{"t":"2024-03-15T10:30:00.123","n":5.0,"m":0.250,"k":5.25,"history":[{"a":1}]}]""");
        var r = h.Run(p, Xlsx(s => s.ArrayHandling = ArrayHandling.SeparateSheets));
        using var wb = OpenXlsx(r.OutputFiles[0]);
        Assert.DoesNotContain(wb.Worksheets, w => w.Name.Equals("History", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wb.Worksheets, w => w.Name == "history_");
        var ws = wb.Worksheet(1);
        var hdr = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        Assert.EndsWith(".000", ws.Cell(2, hdr["t"]).Style.DateFormat.Format);
        Assert.Equal(123, ws.Cell(2, hdr["t"]).GetDateTime().Millisecond);
        Assert.Equal("0.0", ws.Cell(2, hdr["n"]).Style.NumberFormat.Format);
        Assert.Equal(5.0, ws.Cell(2, hdr["n"]).GetDouble());
        Assert.Equal("0.000", ws.Cell(2, hdr["m"]).Style.NumberFormat.Format);
        Assert.NotEqual("0.00", ws.Cell(2, hdr["k"]).Style.NumberFormat.Format);
    }

    [Fact]
    public void NestedLists_KeepTheirGrouping_WhenJoined()
    {
        var p = h.Write("n.json", """[{"id":1,"v":[{"tags":["a","b"]},{"tags":["c"]}],"plain":["p","q"]}]""");
        var rows = ReadCsv(h.Run(p, Csv()).OutputFiles[0]);
        Assert.Equal("[a; b]; c", rows[1][rows[0].IndexOf("v.tags")]);
        Assert.Equal("p; q", rows[1][rows[0].IndexOf("plain")]);   // top-level lists are unchanged
    }
}
