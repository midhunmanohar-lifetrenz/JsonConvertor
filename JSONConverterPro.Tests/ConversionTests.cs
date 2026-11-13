using System.Text;
using JSONConverterPro.Core.Models;
using JSONConverterPro.Core.Services;
using ClosedXML.Excel;
using Xunit;
using static JSONConverterPro.Tests.Harness;

namespace JSONConverterPro.Tests;

public class ConversionTests : IDisposable
{
    private readonly Harness h = new();
    public void Dispose() => h.Dispose();

    private (List<string> header, List<List<string>> rows) Csv1(string file)
    {
        var all = ReadCsv(file);
        return (all[0], all.Skip(1).ToList());
    }

    // ---------- Simple / structure

    [Fact]
    public void SimpleArray_ToCsv_And_Excel_AreEquivalent()
    {
        var p = h.Write("a.json", """[{"id":1,"name":"John","active":true},{"id":2,"name":"Asha","active":false}]""");
        var rc = h.Run(p, Csv());
        var rx = h.Run(p, Xlsx());
        Assert.True(rc.Success, rc.Message);
        Assert.True(rx.Success, rx.Message);

        var (hdr, rows) = Csv1(rc.OutputFiles[0]);
        Assert.Equal(new[] { "id", "name", "active" }, hdr);
        Assert.Equal(new[] { "1", "John", "true" }, rows[0]);

        using var wb = OpenXlsx(rx.OutputFiles[0]);
        var ws = wb.Worksheet(1);
        Assert.Equal("id", ws.Cell(1, 1).GetString());
        Assert.Equal(1.0, ws.Cell(2, 1).GetDouble());          // numbers stay numeric
        Assert.True(ws.Cell(2, 3).GetBoolean());               // booleans stay boolean
        Assert.Equal("Asha", ws.Cell(3, 2).GetString());
        Assert.True(ws.Cell(1, 1).Style.Font.Bold);
        Assert.True(ws.AutoFilter.IsEnabled);
        Assert.Equal(1, ws.SheetView.SplitRow);                // frozen header
        Assert.Equal(rows.Count, ws.LastRowUsed()!.RowNumber() - 1);
    }

    [Fact]
    public void WrapperObject_DataArrayIsDetectedAutomatically()
    {
        var p = h.Write("w.json", """{"status":"success","message":"ok","data":[{"id":1},{"id":2},{"id":3}]}""");
        var info = h.Analyze(p);
        Assert.False(info.RequiresSelection);
        Assert.Equal("data", info.Suggested!.Path);
        Assert.Equal(3, info.Suggested.Count);
        var r = h.Run(p, Csv());
        Assert.Equal(3, r.RecordsProcessed);
    }

    [Fact]
    public void MultipleDataArrays_RequireUserSelection()
    {
        var p = h.Write("m.json", """{"patients":[{"id":1}],"visits":[{"v":1},{"v":2}]}""");
        var info = h.Analyze(p);
        Assert.True(info.RequiresSelection);
        Assert.True(info.Candidates.Count(c => c.Kind == DataSourceKind.ArrayProperty) == 2);
        var pick = info.Candidates.First(c => c.Path == "patients");
        var r = h.Run(p, Csv(), pick);
        Assert.Equal(1, r.RecordsProcessed);
    }

    [Fact]
    public void NestedWrapperPath_IsStreamed()
    {
        var p = h.Write("n.json", """{"result":{"meta":{"x":[1,2]},"items":[{"id":"A"},{"id":"B"}]}}""");
        var info = h.Analyze(p);
        Assert.Equal("result.items", info.Suggested!.Path);
        Assert.Equal(2, h.Run(p, Csv()).RecordsProcessed);
    }

    [Fact]
    public void SingleObject_BecomesOneRecord()
    {
        var p = h.Write("s.json", """{"id":7,"name":"Solo","tags":["a","b"]}""");
        var r = h.Run(p, Csv());
        Assert.True(r.Success, r.Message);
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Single(rows);
        Assert.Equal("a; b", rows[0][hdr.IndexOf("tags")]);
    }

    [Fact]
    public void PrimitiveRootArray_UsesValueColumn()
    {
        var p = h.Write("p.json", "[1,2,3]");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(new[] { "value" }, hdr);
        Assert.Equal(3, rows.Count);
    }

    // ---------- Flattening

    [Fact]
    public void NestedObjects_AreFlattenedWithDotNotation()
    {
        var p = h.Write("f.json", """[{"patientId":1001,"patientName":"John","demographics":{"age":35,"gender":"Male"}}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Contains("demographics.age", hdr);
        Assert.Contains("demographics.gender", hdr);
        Assert.Equal("35", rows[0][hdr.IndexOf("demographics.age")]);
    }

    [Fact]
    public void FlatteningDisabled_KeepsNestedObjectAsJsonText()
    {
        var p = h.Write("f2.json", """[{"id":1,"d":{"age":35}}]""");
        var r = h.Run(p, Csv(s => s.FlattenNestedObjects = false));
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(new[] { "id", "d" }, hdr);
        Assert.Equal("""{"age":35}""", rows[0][1]);
    }

    private const string Sample = """
    {"status":"success","data":[{"patientId":1001,"patientName":"John","demographics":{"age":35,"gender":"Male"},
      "visits":[{"visitId":"V001","visitType":"OP"},{"visitId":"V002","visitType":"IP"}]}]}
    """;

    [Fact]
    public void ArrayOfObjects_Join_KeepsEveryFieldAligned()
    {
        var p = h.Write("v.json", Sample);
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal("V001; V002", rows[0][hdr.IndexOf("visits.visitId")]);
        Assert.Equal("OP; IP", rows[0][hdr.IndexOf("visits.visitType")]);   // nothing silently dropped
        Assert.DoesNotContain("visits", hdr);                                // no stray empty column for the array itself
    }

    [Fact]
    public void ArrayOfObjects_JsonText_KeepsWholeArrayInOneCell()
    {
        var p = h.Write("v2.json", Sample);
        var r = h.Run(p, Csv(s => s.ArrayHandling = ArrayHandling.JsonText));
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Contains("V002", rows[0][hdr.IndexOf("visits")]);
        Assert.Contains("\"IP\"", rows[0][hdr.IndexOf("visits")]);
    }

    [Fact]
    public void ArrayOfObjects_ExpandRows_CreatesOneRowPerElement()
    {
        var p = h.Write("v3.json", Sample);
        var r = h.Run(p, Csv(s => s.ArrayHandling = ArrayHandling.ExpandRows));
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("V001", rows[0][hdr.IndexOf("visits.visitId")]);
        Assert.Equal("V002", rows[1][hdr.IndexOf("visits.visitId")]);
        Assert.Equal("1001", rows[1][hdr.IndexOf("patientId")]);   // parent values repeat
        Assert.Equal(1, r.RecordsProcessed);
        Assert.Equal(2, r.RowsWritten);
    }

    [Fact]
    public void ExpandRows_CapFallsBackToJoinWithWarning()
    {
        var items = string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"x\":{i}}}"));
        var p = h.Write("cap.json", $"[{{\"a\":[{items}],\"b\":[{items}]}}]");
        var r = h.Run(p, Csv(s => { s.ArrayHandling = ArrayHandling.ExpandRows; s.MaxExpandedRowsPerRecord = 500; }));
        Assert.True(r.Success);
        Assert.Equal(1, r.RowsWritten);
        Assert.Contains(r.Warnings, w => w.Contains("combined"));
    }

    [Fact]
    public void SeparateSheets_NestedObjectArrays_GoToLinkedWorksheet()
    {
        var p = h.Write("sep.json", Sample);
        var r = h.Run(p, Xlsx(s => s.ArrayHandling = ArrayHandling.SeparateSheets));
        Assert.True(r.Success, r.Message);
        using var wb = OpenXlsx(r.OutputFiles[0]);
        Assert.Equal(2, wb.Worksheets.Count);
        var main = wb.Worksheet(1);
        var visits = wb.Worksheet(2);
        Assert.Equal("visits", visits.Name);
        Assert.Equal(3, visits.LastRowUsed()!.RowNumber());            // header + 2 visits
        var headers = Enumerable.Range(1, visits.LastColumnUsed()!.ColumnNumber()).Select(i => visits.Cell(1, i).GetString()).ToList();
        Assert.Contains("visitId", headers);
        Assert.Contains("visitType", headers);
        Assert.Contains("_recordNo", headers);
        Assert.Equal("V002", visits.Cell(3, headers.IndexOf("visitId") + 1).GetString());
        Assert.Contains("see 'visits' (2 rows)", main.Cell(2, Enumerable.Range(1, 20).First(i => main.Cell(1, i).GetString() == "visits") ).GetString());
    }

    [Fact]
    public void SeparateSheets_Csv_WritesOneFilePerTable()
    {
        var p = h.Write("sep2.json", Sample);
        var r = h.Run(p, Csv(s => s.ArrayHandling = ArrayHandling.SeparateSheets));
        Assert.Equal(2, r.OutputFiles.Count);
        Assert.Contains(r.OutputFiles, f => f.EndsWith("sep2_visits.csv"));
    }

    // ---------- Null / missing

    [Fact]
    public void MissingProperties_AreBlank_And_NullsDoNotFail()
    {
        var p = h.Write("n.json", """[{"id":1,"a":"x"},{"id":2,"b":null},{"id":3,"a":"y","b":5}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        int a = hdr.IndexOf("a"), b = hdr.IndexOf("b");
        Assert.Equal("", rows[1][a]);   // missing
        Assert.Equal("", rows[1][b]);   // explicit null (blank mode)
        Assert.Equal("5", rows[2][b]);
    }

    [Fact]
    public void LiteralNullMode_DistinguishesNullFromMissing()
    {
        var p = h.Write("n2.json", """[{"id":1,"a":"x"},{"id":2,"a":null}]""");
        var r = h.Run(p, Csv(s => s.NullHandling = NullHandling.LiteralNull));
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal("x", rows[0][hdr.IndexOf("a")]);
        Assert.Equal("null", rows[1][hdr.IndexOf("a")]);
    }

    [Fact]
    public void EmptyObjectsAndArrays_KeepTheirColumns()
    {
        var p = h.Write("e.json", """[{"id":1,"o":{},"a":[]}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Contains("o", hdr); Assert.Contains("a", hdr);
    }

    // ---------- Data-integrity rules

    [Fact]
    public void LargeIdsAndLeadingZeros_ArePreserved_InExcel()
    {
        var p = h.Write("ids.json", """[{"id":12345678901234567890,"mrn":"000123","amount":12.5,"big":123456789012}]""");
        var r = h.Run(p, Xlsx());
        using var wb = OpenXlsx(r.OutputFiles[0]);
        var ws = wb.Worksheet(1);
        // Locate columns by header, because smart ordering may move them.
        int Col(string n) => Enumerable.Range(1, 4).First(i => ws.Cell(1, i).GetString() == n);
        Assert.Equal("12345678901234567890", ws.Cell(2, Col("id")).GetString());   // text, no scientific notation
        Assert.Equal("000123", ws.Cell(2, Col("mrn")).GetString());                // leading zeros kept
        Assert.Equal(12.5, ws.Cell(2, Col("amount")).GetDouble());
        Assert.Equal("0", ws.Cell(2, Col("big")).Style.NumberFormat.Format);       // no 1.2E+11
    }

    [Fact]
    public void LargeIdsAndLeadingZeros_ArePreserved_InCsv()
    {
        var p = h.Write("ids2.json", """[{"id":12345678901234567890,"mrn":"000123","d":1.50}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal("12345678901234567890", rows[0][hdr.IndexOf("id")]);
        Assert.Equal("000123", rows[0][hdr.IndexOf("mrn")]);
        Assert.Equal("1.50", rows[0][hdr.IndexOf("d")]);
    }

    [Fact]
    public void Dates_OnlyUnambiguousIsoValuesBecomeDates()
    {
        var p = h.Write("d.json", """[{"iso":"2024-03-15","isoTime":"2024-03-15T10:30:00","amb":"01/02/2024","num":"20240315","zulu":"2024-03-15T10:30:00Z"}]""");
        var r = h.Run(p, Xlsx());
        using var wb = OpenXlsx(r.OutputFiles[0]);
        var ws = wb.Worksheet(1);
        int Col(string n) => Enumerable.Range(1, 5).First(i => ws.Cell(1, i).GetString() == n);
        Assert.Equal(XLDataType.DateTime, ws.Cell(2, Col("iso")).DataType);
        Assert.Equal(XLDataType.DateTime, ws.Cell(2, Col("isoTime")).DataType);
        Assert.Equal(XLDataType.Text, ws.Cell(2, Col("amb")).DataType);
        Assert.Equal("20240315", ws.Cell(2, Col("num")).GetString());
        Assert.Equal(XLDataType.Text, ws.Cell(2, Col("zulu")).DataType);
    }

    [Fact]
    public void Unicode_IsPreserved_InBothFormats()
    {
        const string text = "नमस्ते · 患者 · مريض · Zoë 😀";
        var p = h.Write("u.json", $"[{{\"name\":\"{text}\"}}]");
        var c = h.Run(p, Csv());
        var x = h.Run(p, Xlsx());
        var bytes = File.ReadAllBytes(c.OutputFiles[0]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());   // UTF-8 BOM
        Assert.Equal(text, Csv1(c.OutputFiles[0]).rows[0][0]);
        using var wb = OpenXlsx(x.OutputFiles[0]);
        Assert.Equal(text, wb.Worksheet(1).Cell(2, 1).GetString());
    }

    [Fact]
    public void Utf8Bom_And_Utf16Input_AreAccepted()
    {
        var a = h.Write("bom.json", "[{\"n\":\"é\"}]", new UTF8Encoding(true));
        var b = h.Write("u16.txt", "[{\"n\":\"é\"}]", Encoding.Unicode);
        Assert.Equal("é", Csv1(h.Run(a, Csv()).OutputFiles[0]).rows[0][0]);
        Assert.Equal("é", Csv1(h.Run(b, Csv()).OutputFiles[0]).rows[0][0]);
    }

    // ---------- Column collisions

    [Fact]
    public void FlattenedNameCollision_NeverOverwritesData()
    {
        var p = h.Write("c.json", """[{"a.b":1,"a":{"b":2}}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(2, hdr.Count);
        Assert.Equal(new[] { "1", "2" }, rows[0]);
        Assert.Equal(2, hdr.Distinct().Count());
        Assert.Contains(r.Warnings, w => w.Contains("collided"));
    }

    [Fact]
    public void DuplicatePropertyNames_AreBothKept()
    {
        var p = h.Write("dup.json", """[{"id":1,"id":2}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        Assert.Equal(2, hdr.Count);
        Assert.Equal(new[] { "1", "2" }, rows[0].OrderBy(x => x).ToArray());
    }

    // ---------- Ordering

    private const string Unordered = """[{"zeta":1,"status":"A","createdDate":"2024-01-01","description":"d","patientName":"n","type":"t","patientCode":"c","patientId":9,"alpha":2}]""";

    [Fact]
    public void SmartOrdering_FollowsTheSpecifiedPriority()
    {
        var p = h.Write("o1.json", Unordered);
        var hdr = Csv1(h.Run(p, Csv()).OutputFiles[0]).header;
        Assert.Equal(new[] { "patientId", "patientCode", "patientName", "description", "createdDate", "status", "type", "zeta", "alpha" }, hdr);
    }

    [Fact]
    public void AlphabeticalAndOriginalAndCustomOrdering()
    {
        var p = h.Write("o2.json", Unordered);
        var alpha = Csv1(h.Run(p, Csv(s => s.ColumnOrdering = ColumnOrdering.Alphabetical)).OutputFiles[0]).header;
        Assert.Equal("alpha", alpha[0]);
        var orig = Csv1(h.Run(p, Csv(s => s.ColumnOrdering = ColumnOrdering.Original)).OutputFiles[0]).header;
        Assert.Equal(new[] { "zeta", "status" }, orig.Take(2).ToArray());
        var custom = Csv1(h.Run(p, Csv(s => { s.ColumnOrdering = ColumnOrdering.Custom; s.CustomColumnOrder = new() { "alpha", "type" }; })).OutputFiles[0]).header;
        Assert.Equal(new[] { "alpha", "type", "zeta" }, custom.Take(3).ToArray());
    }

    // ---------- CSV format

    [Fact]
    public void Csv_EscapesCommasQuotesAndNewlines_AndSupportsDelimiters()
    {
        var p = h.Write("esc.json", "[{\"a\":\"x,y\",\"b\":\"say \\\"hi\\\"\",\"c\":\"line1\\nline2\",\"d\":\"p;q\"}]");
        var c = h.Run(p, Csv());
        var (_, rows) = Csv1(c.OutputFiles[0]);
        Assert.Equal(new[] { "x,y", "say \"hi\"", "line1\nline2", "p;q" }, rows[0]);

        var semi = h.Run(p, Csv(s => s.CsvDelimiter = CsvDelimiter.Semicolon));
        var rowsSemi = ReadCsv(semi.OutputFiles[0], ';');
        Assert.Equal(new[] { "x,y", "say \"hi\"", "line1\nline2", "p;q" }, rowsSemi[1]);

        var tab = h.Run(p, Csv(s => s.CsvDelimiter = CsvDelimiter.Tab));
        Assert.Equal(4, ReadCsv(tab.OutputFiles[0], '\t')[1].Count);
    }

    // ---------- Security

    [Fact]
    public void Csv_ProtectsAgainstFormulaInjection_ButNeverTouchesNumbers()
    {
        var p = h.Write("inj.json", """[{"a":"=HYPERLINK(\"http://x\")","b":"@SUM(A1)","c":"-5.2","d":"+91 98765 43210","e":"plain","f":-7,"g":"-cmd|' /C calc'!A0"}]""");
        var r = h.Run(p, Csv());
        var (hdr, rows) = Csv1(r.OutputFiles[0]);
        string V(string c) => rows[0][hdr.IndexOf(c)];
        Assert.StartsWith("'=", V("a"));
        Assert.StartsWith("'@", V("b"));
        Assert.Equal("-5.2", V("c"));              // numeric text untouched
        Assert.Equal("+91 98765 43210", V("d"));   // a sign followed only by digits and spaces can't be a formula: left as is
        Assert.Equal("plain", V("e"));
        Assert.Equal("-7", V("f"));                // real JSON numbers untouched
        Assert.StartsWith("'-cmd", V("g"));

        var off = h.Run(p, Csv(s => s.ProtectCsvFormulas = false));
        var (h2, r2) = Csv1(off.OutputFiles[0]);
        Assert.StartsWith("=HYPERLINK", r2[0][h2.IndexOf("a")]);
    }

    [Fact]
    public void Excel_StoresFormulaLookingTextAsText_NotAsFormula()
    {
        var p = h.Write("inj2.json", """[{"a":"=1+1","b":"@SUM(A1)"}]""");
        var r = h.Run(p, Xlsx());
        using var wb = OpenXlsx(r.OutputFiles[0]);
        var cell = wb.Worksheet(1).Cell(2, 1);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal("=1+1", cell.GetString());
    }

    // ---------- Error handling

    [Fact]
    public void InvalidJson_GivesFriendlyError_NoCrash_NoOutput()
    {
        var p = h.Write("bad.json", """[{"id":1},{"id":2,]""");
        var ex = Assert.Throws<UserFacingException>(() => h.Analyze(p));
        Assert.Contains("not valid JSON", ex.Message);

        var p2 = h.Write("bad2.json", """{"id":1""");
        Assert.Throws<UserFacingException>(() => h.Analyze(p2));
    }

    [Fact]
    public void EmptyFile_And_WhitespaceOnly_AreRejectedFriendly()
    {
        var p = h.Write("empty.json", "");
        var ex = Assert.Throws<UserFacingException>(() => h.Files.ValidateInput(p));
        Assert.Contains("empty", ex.Message);
        var p2 = h.Write("ws.json", "   \n  ");
        Assert.True(h.Analyze(p2).IsEmpty);
    }

    [Fact]
    public void UnsupportedExtension_And_MissingFile_AreRejected()
    {
        var p = h.Write("x.csv", "a,b");
        Assert.Contains("not supported", Assert.Throws<UserFacingException>(() => h.Files.ValidateInput(p)).Message);
        Assert.Contains("could not be found", Assert.Throws<UserFacingException>(() => h.Files.ValidateInput(Path.Combine(h.Dir, "nope.json"))).Message);
    }

    [Fact]
    public void EmptyDataArray_ReportsNoRecords()
    {
        var p = h.Write("zero.json", "[]");
        var r = h.Run(p, Csv(), new DataSourceCandidate(DataSourceKind.RootArray, Array.Empty<string>(), 0, false));
        Assert.False(r.Success);
        Assert.Contains("no records", r.Message);
    }

    [Fact]
    public void InvalidJsonDuringConversion_ReturnsFailureResult_NotException()
    {
        var p = h.Write("late.json", """[{"id":1},{"id":2},{"id":""" );
        var r = h.Conv.Convert(new ConversionRequest
        {
            InputPath = p, OutputDirectory = Path.Combine(h.Dir, "out"), Settings = Csv(),
            Source = new DataSourceCandidate(DataSourceKind.RootArray, Array.Empty<string>(), 0, true)
        }, null, default);
        Assert.False(r.Success);
        Assert.Contains("not valid JSON", r.Message);
        Assert.Empty(Directory.Exists(Path.Combine(h.Dir, "out")) ? Directory.GetFiles(Path.Combine(h.Dir, "out")) : Array.Empty<string>());
    }

    // ---------- Overwrite / cancellation / batch

    [Fact]
    public void ExistingOutput_RequiresConfirmation_BeforeOverwrite()
    {
        var p = h.Write("ow.json", """[{"id":1}]""");
        var first = h.Run(p, Csv());
        Assert.True(first.Success);
        File.WriteAllText(first.OutputFiles[0], "SENTINEL");

        var denied = h.Run(p, Csv(), confirm: _ => false);
        Assert.False(denied.Success);
        Assert.Contains("already exists", denied.Message);
        Assert.Equal("SENTINEL", File.ReadAllText(first.OutputFiles[0]));   // untouched

        IReadOnlyList<string>? asked = null;
        var ok = h.Run(p, Csv(), confirm: l => { asked = l; return true; });
        Assert.True(ok.Success);
        Assert.Single(asked!);
        Assert.NotEqual("SENTINEL", File.ReadAllText(first.OutputFiles[0]));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Dir, "out"), "*.tmp"));
    }

    [Fact]
    public void Cancellation_StopsSafely_AndLeavesNoFiles()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 50_000; i++) { if (i > 0) sb.Append(','); sb.Append($"{{\"id\":{i},\"name\":\"name {i}\",\"n\":{{\"a\":{i}}}}}"); }
        sb.Append(']');
        var p = h.Write("cancel.json", sb.ToString());
        using var cts = new CancellationTokenSource();
        var info = h.Analyze(p);
        int calls = 0;
        var progress = new Progress<ConversionProgress>(_ => { });
        var r = h.Conv.Convert(new ConversionRequest
        {
            InputPath = p, Source = info.Suggested!, Settings = Csv(), OutputDirectory = Path.Combine(h.Dir, "out"),
            ConfirmOverwrite = _ => { cts.Cancel(); return true; }
        }, new SyncProgress(_ => calls++), cts.Token);
        // Not reached via confirm (no existing file) – cancel directly instead:
        cts.Cancel();
        var r2 = h.Conv.Convert(new ConversionRequest
        {
            InputPath = p, Source = info.Suggested!, Settings = Xlsx(), OutputDirectory = Path.Combine(h.Dir, "out2")
        }, null, cts.Token);
        Assert.True(r2.Cancelled);
        Assert.False(r2.Success);
        var dir = Path.Combine(h.Dir, "out2");
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
    }

    private sealed class SyncProgress : IProgress<ConversionProgress>
    {
        private readonly Action<ConversionProgress> _a;
        public SyncProgress(Action<ConversionProgress> a) { _a = a; }
        public void Report(ConversionProgress value) => _a(value);
    }

    [Fact]
    public void CancelMidWrite_DeletesTemporaryFiles()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 20_000; i++) { if (i > 0) sb.Append(','); sb.Append($"{{\"id\":{i},\"name\":\"name {i}\"}}"); }
        sb.Append(']');
        var p = h.Write("mid.json", sb.ToString());
        var info = h.Analyze(p);
        using var cts = new CancellationTokenSource();
        var r = h.Conv.Convert(new ConversionRequest
        {
            InputPath = p, Source = info.Suggested!, Settings = Csv(), OutputDirectory = Path.Combine(h.Dir, "outc")
        }, new SyncProgress(pr => { if (pr.Phase.StartsWith("Writing")) cts.Cancel(); }), cts.Token);
        Assert.True(r.Cancelled);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Dir, "outc")));
    }

    [Fact]
    public void MultipleFiles_ConvertIndependently()
    {
        var a = h.Write("one.json", """[{"x":1,"onlyA":true}]""");
        var b = h.Write("two.json", """{"data":[{"y":2},{"y":3}]}""");
        var bad = h.Write("three.json", "{oops");
        var ra = h.Run(a, Csv());
        var rb = h.Run(b, Csv());
        Assert.True(ra.Success && rb.Success);
        Assert.Throws<UserFacingException>(() => h.Run(bad, Csv()));
        Assert.Equal(new[] { "x", "onlyA" }, ReadCsv(ra.OutputFiles[0])[0]);
        Assert.Equal(new[] { "y" }, ReadCsv(rb.OutputFiles[0])[0]);   // no leakage between files
        Assert.Equal(3, ReadCsv(rb.OutputFiles[0]).Count);
    }

    // ---------- Preview

    [Fact]
    public void Preview_ShowsFirstTenRecords_WithTotals_AndDoesNotLeakBetweenFiles()
    {
        var p1 = h.Write("pv1.json", "[" + string.Join(",", Enumerable.Range(1, 25).Select(i => $"{{\"id\":{i},\"a\":\"x\"}}")) + "]");
        var p2 = h.Write("pv2.json", """[{"other":1}]""");
        var v1 = h.Preview.Build(p1, h.Analyze(p1).Suggested!, new ConversionSettings(), 10, default);
        Assert.Equal(10, v1.Rows.Count);
        Assert.Equal(25, v1.TotalRecords);
        Assert.Equal(2, v1.TotalColumns);
        var v2 = h.Preview.Build(p2, h.Analyze(p2).Suggested!, new ConversionSettings(), 10, default);
        Assert.Equal(new[] { "other" }, v2.ColumnNames);
        Assert.Single(v2.Rows);
    }

    // ---------- Excel limits

    [Fact]
    public void Excel_SplitsIntoAdditionalWorksheets_WhenRowLimitIsReached()
    {
        var p = h.Write("split.json", "[" + string.Join(",", Enumerable.Range(1, 25).Select(i => $"{{\"id\":{i}}}")) + "]");
        var r = h.Run(p, Xlsx(s => s.MaxRowsPerSheet = 10));
        using var wb = OpenXlsx(r.OutputFiles[0]);
        Assert.Equal(3, wb.Worksheets.Count);
        Assert.Equal("Data", wb.Worksheet(1).Name);
        Assert.Equal("Data (2)", wb.Worksheet(2).Name);
        Assert.Equal(11, wb.Worksheet(1).LastRowUsed()!.RowNumber());
        Assert.Equal(6, wb.Worksheet(3).LastRowUsed()!.RowNumber());   // header + 5
        Assert.Equal("id", wb.Worksheet(3).Cell(1, 1).GetString());
        Assert.Equal(25.0, wb.Worksheet(3).Cell(6, 1).GetDouble());
    }

    [Fact]
    public void Excel_SheetNameIsSanitisedAndLimitedTo31Chars()
    {
        var p = h.Write("sn.json", """[{"id":1}]""");
        var r = h.Run(p, Xlsx(s => s.SheetName = "Bad:Name/with*chars that is far too long for Excel"));
        using var wb = OpenXlsx(r.OutputFiles[0]);
        var name = wb.Worksheet(1).Name;
        Assert.True(name.Length <= 31);
        Assert.DoesNotContain(':', name);
    }
}
