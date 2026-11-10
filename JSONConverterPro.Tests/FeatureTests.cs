using System.Text;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;
using JSONConverterPro.Core.Services;
using Xunit;
using static JSONConverterPro.Tests.Harness;

namespace JSONConverterPro.Tests;

public class FeatureTests : IDisposable
{
    private readonly Harness h = new();
    public void Dispose() => h.Dispose();

    private (List<string> header, List<List<string>> rows) Csv1(string file)
    {
        var all = ReadCsv(file);
        return (all[0], all.Skip(1).ToList());
    }

    private const string Patients = """[{"id":1,"name":"Asha Nair","phone":"+91 98765 43210","city":"Kochi","visits":[{"visitId":"V1","doctor":"Dr Rao"}]},{"id":2,"name":"John","phone":null,"city":"Kochi","visits":[{"visitId":"V2","doctor":"Dr Rao"}]}]""";

    // ---------- Column rules

    [Fact]
    public void ColumnRules_ExcludeRenameAndMask_ApplyToExport()
    {
        var p = h.Write("r.json", Patients);
        var r = h.Run(p, Csv(s => s.ColumnRules = new()
        {
            new ColumnRule { Name = "city", Include = false },
            new ColumnRule { Name = "name", RenameTo = "Patient name", Mask = MaskMode.Redact },
            new ColumnRule { Name = "phone", Mask = MaskMode.Hash }
        }));
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.DoesNotContain("city", hdr);
        Assert.Contains("Patient name", hdr);
        Assert.DoesNotContain("name", hdr);
        Assert.Equal("[REDACTED]", rows[0][hdr.IndexOf("Patient name")]);
        string hash0 = rows[0][hdr.IndexOf("phone")];
        Assert.Matches("^[0-9a-f]{16}$", hash0);
        Assert.DoesNotContain("98765", hash0);
        Assert.Equal("", rows[1][hdr.IndexOf("phone")]);            // null stays null, not hashed
        Assert.Equal("1", rows[0][hdr.IndexOf("id")]);               // untouched columns unchanged
    }

    [Fact]
    public void Hash_IsConsistentWithinARun_AndDifferentValuesDiffer()
    {
        var p = h.Write("h.json", """[{"x":"same"},{"x":"same"},{"x":"other"}]""");
        var r = h.Run(p, Csv(s => s.ColumnRules = new() { new ColumnRule { Name = "x", Mask = MaskMode.Hash } }));
        var (_, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(rows[0][0], rows[1][0]);
        Assert.NotEqual(rows[0][0], rows[2][0]);
    }

    [Fact]
    public void ColumnRules_AlsoMaskLinkedSheets_SoNothingLeaksViaChildTables()
    {
        var p = h.Write("rc.json", Patients);
        var r = h.Run(p, Xlsx(s => { s.ArrayHandling = ArrayHandling.SeparateSheets; s.ColumnRules = new() { new ColumnRule { Name = "doctor", Mask = MaskMode.Redact } }; }));
        using var wb = OpenXlsx(r.OutputFiles[0]);
        var visits = wb.Worksheet(2);
        int col = Enumerable.Range(1, 6).First(i => visits.Cell(1, i).GetString() == "doctor");
        Assert.Equal("[REDACTED]", visits.Cell(2, col).GetString());
    }

    [Fact]
    public void RenameCollision_GetsSuffix_AndAllExcludedIsAFriendlyError()
    {
        var p = h.Write("rn.json", """[{"a":1,"b":2}]""");
        var r = h.Run(p, Csv(s => s.ColumnRules = new() { new ColumnRule { Name = "b", RenameTo = "a" } }));
        Assert.Equal(new[] { "a", "a (2)" }, Csv1(r.OutputFiles[0]).header);

        var bad = h.Run(p, Csv(s => s.ColumnRules = new() { new ColumnRule { Name = "a", Include = false }, new ColumnRule { Name = "b", Include = false } }));
        Assert.False(bad.Success);
        Assert.Contains("Every column is excluded", bad.Message);
    }

    [Fact]
    public void Preview_UsesTheSameRules_AndReportsStructure()
    {
        var p = h.Write("pv.json", Patients);
        var settings = new ConversionSettings { ColumnRules = new() { new ColumnRule { Name = "name", Mask = MaskMode.Redact }, new ColumnRule { Name = "city", Include = false } } };
        var v = h.Preview.Build(p, h.Analyze(p).Suggested!, settings, 10, default);
        Assert.DoesNotContain("city", v.ColumnNames);
        Assert.Equal("[REDACTED]", v.Rows[0][v.ColumnNames.ToList().IndexOf("name")]);
        // Structure still lists every column (even excluded) so it can be re-enabled
        Assert.Contains(v.Structure, c => c.Name == "city");
        var phone = v.Structure.Single(c => c.Name == "phone");
        Assert.Equal("text", phone.Type);
        Assert.Equal(50, phone.FilledPercent);
        Assert.Equal(1, phone.Nulls);
        Assert.Equal("+91 98765 43210", phone.Sample);
        Assert.Equal("number", v.Structure.Single(c => c.Name == "id").Type);
        Assert.Equal(2, v.Structure.Single(c => c.Name == "visits.visitId").Depth);
    }

    // ---------- JSON Lines

    [Fact]
    public void JsonLines_AreReadAsRecords_IncludingBlankLinesAndCrLf()
    {
        var p = h.Write("data.jsonl", "{\"id\":1,\"n\":\"a\"}\r\n\r\n{\"id\":2,\"n\":\"b\"}\n{\"id\":3,\"n\":\"é\"}\n\n");
        var info = h.Analyze(p);
        Assert.Equal(3, info.Suggested!.Count);
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("é", rows[2][hdr.IndexOf("n")]);
    }

    [Fact]
    public void JsonLines_WithBomAndTxtExtension_AndPrettyPrintedJsonIsNotMisdetected()
    {
        var jl = h.Write("lines.txt", "{\"a\":1}\n{\"a\":2}", new UTF8Encoding(true));
        Assert.Equal(2, h.Analyze(jl).Suggested!.Count);

        var pretty = h.Write("pretty.json", "{\n  \"a\": 1,\n  \"b\": [\n    {\"x\":1}\n  ]\n}");
        var arr = h.Write("arr.json", "[\n{\"a\":1},\n{\"a\":2}\n]");
        Assert.Equal(DataSourceKind.ArrayProperty, h.Analyze(pretty).Suggested!.Kind);
        Assert.Equal(2, h.Analyze(arr).Suggested!.Count);
    }

    [Fact]
    public void JsonLines_ErrorReportsTheOriginalLineNumber()
    {
        var p = h.Write("bad.jsonl", "{\"a\":1}\n{\"a\":2}\n{\"a\":3,}x\n{\"a\":4}\n");
        var ex = Assert.Throws<UserFacingException>(() => h.Analyze(p));
        Assert.Equal(3, ex.Line);
        Assert.Contains("line 3", ex.Message);
    }

    [Fact]
    public void SyntaxError_ExposesLineAndColumn()
    {
        var p = h.Write("e.json", "{\n  \"a\": 1,\n  \"b\": ]\n}");
        var ex = Assert.Throws<UserFacingException>(() => h.Analyze(p));
        Assert.Equal(3, ex.Line);
        Assert.True(ex.Column > 0);
    }

    // ---------- Output options

    [Fact]
    public void CsvEncodings_Utf8NoBom_And_Utf16()
    {
        var p = h.Write("enc.json", """[{"n":"नमस्ते"}]""");
        var plain = h.Run(p, Csv(s => s.CsvEncoding = CsvEncoding.Utf8));
        Assert.NotEqual(0xEF, File.ReadAllBytes(plain.OutputFiles[0])[0]);
        Assert.Contains("नमस्ते", File.ReadAllText(plain.OutputFiles[0], new UTF8Encoding(false)));

        var u16 = h.Run(p, Csv(s => s.CsvEncoding = CsvEncoding.Utf16));
        var bytes = File.ReadAllBytes(u16.OutputFiles[0]);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2).ToArray());
        Assert.Contains("नमस्ते", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void ArrayJoinSeparator_IsConfigurable()
    {
        var p = h.Write("sep.json", """[{"tags":["a","b","c"]}]""");
        var r = h.Run(p, Csv(s => s.ArrayJoinSeparator = " | "));
        Assert.Equal("a | b | c", Csv1(r.OutputFiles[0]).rows[0][0]);
    }
}
