using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JSONConverterPro.Models;
using JSONConverterPro.Services;
using JSONConverterPro.ViewModels;
using JSONConverterPro.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(JSONConverterPro.Tests.Ui.TestAppBuilder))]

namespace JSONConverterPro.Tests.Ui;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("JCP_STATE_DIR", Path.Combine(Path.GetTempPath(), "jcp-ui-state-" + Guid.NewGuid().ToString("N")[..6]));
        return AppBuilder.Configure<JSONConverterPro.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>Stands in for the system file dialogs: records what was asked and answers with preset paths.</summary>
public sealed class FakeDialogs : IDialogService
{
    public string? Folder { get; set; }
    public Queue<string?> SavePaths { get; } = new();
    public List<string> SaveRequests { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;
    public int FolderPrompts { get; private set; }
    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<string?> PickFolderAsync(string? startFolder) { FolderPrompts++; return Task.FromResult(Folder); }
    public Task<string?> SaveFileAsync(string suggestedName, string extension, string typeLabel, string? startFolder)
    {
        SaveRequests.Add(suggestedName + "." + extension);
        return Task.FromResult(SavePaths.Count > 0 ? SavePaths.Dequeue() : null);
    }
    public Task<bool> ConfirmAsync(string title, string message, string yes, string no) => Task.FromResult(ConfirmAnswer);
}

public class UiTests
{
    private static string Samples
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent;
            return Path.Combine(d!.FullName, "samples");
        }
    }

    private static string ShotDir => Environment.GetEnvironmentVariable("JCP_SHOTS") ?? Path.Combine(Path.GetTempPath(), "jcp-shots");

    // Headless tests don't pump the dispatcher while awaiting, so spin it explicitly.
    private static void Pump(Task t, int timeoutMs = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < timeoutMs) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(t.IsCompleted, "task did not complete");
        t.GetAwaiter().GetResult();
    }

    private static void Spin(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    private static void Settle(MainViewModel vm)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int stable = 0;
        while (sw.ElapsedMilliseconds < 30000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
            bool idle = !vm.PreviewBusy && vm.Files.All(f => f.Status != FileStatus.Analyzing);
            stable = idle ? stable + 1 : 0;
            if (stable > 8) break;
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Types text into the paste area and waits for the live preview (the debounce is bypassed by refreshing directly).</summary>
    private static void SetPaste(MainViewModel vm, string text)
    {
        vm.PasteText = text;
        Pump(vm.RefreshPasteAsync());
        Spin(700);   // let any queued debounced refresh finish so it can't race the assertions
    }

    private static void Shot(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("JCP_NOSHOT") == "1") return;
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, name + ".png"));
    }

    // One window is shared by all UI tests (constructing a second window in the same headless session stalls);
    // every test still gets its own fresh object graph, view model and fake dialogs.
    private static MainWindow? _shared;

    private static (MainWindow win, MainViewModel vm, FakeDialogs dlg) Create()
    {
        var dlg = new FakeDialogs();
        var sp = App.BuildServices(sc => sc.AddSingleton<IDialogService>(dlg));
        var vm = sp.GetRequiredService<MainViewModel>();
        if (_shared == null)
        {
            _shared = new MainWindow { Width = 1180, Height = 780 };
            _shared.Show();
        }
        _shared.DataContext = vm;
        Dispatcher.UIThread.RunJobs();
        return (_shared, vm, dlg);
    }

    private const string Hospital = """
    {"status":"success","message":"OK","data":[
      {"patientId":1001,"mrn":"000123","patientName":"John","demographics":{"age":35,"gender":"Male"},
       "visits":[{"visitId":"V001","visitType":"OP"},{"visitId":"V002","visitType":"IP"}]},
      {"patientId":1002,"mrn":"000124","patientName":"Zoë","demographics":{"age":29,"gender":"Female"},"visits":[{"visitId":"V003","visitType":"OP"}]}]}
    """;

    // ------------------------------------------------------------------ paste-first screen

    [AvaloniaFact]
    public void PasteScreen_LivePreview_Downloads_AndErrors()
    {
        var (win, vm, dlg) = Create();
        Assert.True(vm.IsConvertPage);                     // first screen is the paste area
        Shot(win, "10-convert-empty");
        Assert.False(vm.PasteHasPreview);
        Assert.Equal("idle", vm.PasteStatusKind);

        // Valid paste -> live preview on the same screen
        SetPaste(vm, Hospital);
        Assert.True(vm.PasteIsOk, vm.PasteStatusText);
        Assert.Contains("2 records", vm.PasteStatusText);
        Assert.Equal(2, vm.PastePreviewRows.Count);
        Assert.Equal("patientId", vm.PastePreviewColumns[0]);
        var grid = win.FindControl<DataGrid>("PastePreviewGrid")!;
        Assert.Equal(vm.PastePreviewColumns.Count, grid.Columns.Count);
        Shot(win, "11-convert-valid");

        // Download Excel: Save As dialog decides the location; nothing is written without it
        Pump(vm.DownloadExcelCommand.ExecuteAsync(null));
        Assert.Single(dlg.SaveRequests);                   // cancelled dialog (no queued path) -> nothing saved
        Assert.False(vm.ShowBanner);

        string dir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string xlsx = Path.Combine(dir, "patients.xlsx");
        dlg.SavePaths.Enqueue(xlsx);
        Pump(vm.DownloadExcelCommand.ExecuteAsync(null));
        Assert.True(File.Exists(xlsx));
        Assert.Equal("success", vm.BannerKind);
        using (var wb = Harness.OpenXlsx(xlsx))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("patientId", ws.Cell(1, 1).GetString());
            Assert.Equal(1001.0, ws.Cell(2, 1).GetDouble());
            Assert.Equal("Zoë", Enumerable.Range(1, 12).Select(c => (c, h: ws.Cell(1, c).GetString())).Where(x => x.h == "patientName").Select(x => ws.Cell(3, x.c).GetString()).Single());
            Assert.Equal(3, ws.LastRowUsed()!.RowNumber());
        }
        Shot(win, "12-convert-saved");

        // Download CSV to a name without extension -> .csv is applied, same data
        string csvTarget = Path.Combine(dir, "patients");
        dlg.SavePaths.Enqueue(csvTarget + ".csv");
        Pump(vm.DownloadCsvCommand.ExecuteAsync(null));
        var rows = Harness.ReadCsv(Path.Combine(dir, "patients.csv"));
        Assert.Equal(3, rows.Count);
        Assert.Equal("patientId", rows[0][0]);
        Assert.Equal("1002", rows[2][0]);
        Assert.Contains("V001; V002", rows[1]);

        // Invalid JSON -> error shown, download blocked (no Save As prompt)
        SetPaste(vm, """[{"id":1},{"id":2,]""");
        Assert.True(vm.PasteIsError);
        Assert.Contains("not valid JSON", vm.PasteStatusText);
        Assert.False(vm.PasteHasPreview);
        Assert.Empty(vm.PastePreviewRows);
        int before = dlg.SaveRequests.Count;
        Pump(vm.DownloadCsvCommand.ExecuteAsync(null));
        Assert.Equal(before, dlg.SaveRequests.Count);
        Assert.Equal("warning", vm.BannerKind);
        Shot(win, "13-convert-error");

        // Several arrays -> must choose, then preview appears
        SetPaste(vm, """{"patients":[{"id":1}],"visits":[{"v":1},{"v":2}]}""");
        Assert.Equal("warn", vm.PasteStatusKind);
        Assert.True(vm.ShowPasteCandidates);
        Assert.False(vm.PasteHasPreview);
        vm.SelectedPasteCandidate = vm.PasteCandidates.First(c => c.Path == "visits");
        Spin(300); Pump(vm.RefreshPasteAsync()); Spin(700);
        Assert.True(vm.PasteIsOk, vm.PasteStatusText);
        Assert.Equal(2, vm.PastePreviewRows.Count);
        Assert.Equal(new[] { "v" }, vm.PastePreviewColumns);

        // Empty text resets everything
        SetPaste(vm, "");
        Assert.Equal("idle", vm.PasteStatusKind);
        Assert.False(vm.PasteHasPreview);

        // Settings change updates the live preview
        SetPaste(vm, Hospital);
        vm.OrderIndex = 1;                                 // alphabetical
        Spin(900);
        Assert.Equal("demographics.age", vm.PastePreviewColumns[0]);

        Directory.Delete(dir, true);
    }

    [AvaloniaFact]
    public void PasteScreen_ExistingFile_IsOverwrittenOnlyAfterSaveDialog()
    {
        var (win, vm, dlg) = Create();
        string dir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string csv = Path.Combine(dir, "out.csv");
        File.WriteAllText(csv, "OLD");
        SetPaste(vm, """[{"a":1}]""");
        dlg.SavePaths.Enqueue(csv);                        // the OS dialog already confirmed overwriting this file
        Pump(vm.DownloadCsvCommand.ExecuteAsync(null));
        Assert.Contains("a", File.ReadAllText(csv));
        Assert.DoesNotContain("OLD", File.ReadAllText(csv));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Directory.Delete(dir, true);
    }

    // ------------------------------------------------------------------ files (batch) pages

    [AvaloniaFact]
    public void Files_AddPreviewConvert_AsksForFolderEveryTime()
    {
        var (win, vm, dlg) = Create();
        string outDir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        dlg.Folder = outDir;
        vm.CurrentPage = "Files";

        Pump(vm.AddPathsAsync(new[]
        {
            Path.Combine(Samples, "02-his-patients-nested.json"), Path.Combine(Samples, "01-simple-array.json"),
            Path.Combine(Samples, "04-multiple-arrays-choose.json"), Path.Combine(Samples, "90-invalid.json"),
            Path.Combine(Samples, "91-empty.json"), Path.Combine(Samples, "notes.docx")
        }));
        Settle(vm);
        Shot(win, "02-files");

        var byName = vm.Files.ToDictionary(f => f.Name);
        Assert.Equal(FileStatus.Ready, byName["02-his-patients-nested.json"].Status);
        Assert.Equal(FileStatus.Ready, byName["01-simple-array.json"].Status);
        Assert.Equal(FileStatus.NeedsSelection, byName["04-multiple-arrays-choose.json"].Status);
        Assert.Equal(FileStatus.Invalid, byName["90-invalid.json"].Status);
        Assert.Contains("not valid JSON", byName["90-invalid.json"].Detail);
        Assert.Equal(FileStatus.Invalid, byName["91-empty.json"].Status);
        Assert.Equal(FileStatus.Invalid, byName["notes.docx"].Status);

        vm.CurrentPage = "Preview";
        Settle(vm);
        Shot(win, "03-preview");
        Assert.True(vm.HasPreview);
        Assert.Equal(2, vm.PreviewRows.Count);
        Assert.Equal("patientId", vm.PreviewColumns[0]);

        vm.SelectFile(byName["01-simple-array.json"]);
        Assert.False(vm.HasPreview);                       // old records cleared immediately
        Settle(vm);
        Assert.Equal(3, vm.PreviewRows.Count);
        Assert.DoesNotContain("patientId", vm.PreviewColumns);

        vm.SelectFile(byName["04-multiple-arrays-choose.json"]);
        Settle(vm);
        Assert.True(vm.ShowCandidates);
        Assert.False(vm.HasPreview);
        vm.SelectedCandidate = vm.Candidates.First(c => c.Path == "appointments");
        Settle(vm);
        Assert.Equal(3, vm.PreviewRows.Count);

        foreach (var bad in vm.Files.Where(f => f.Status == FileStatus.Invalid).ToList()) vm.RemoveFile(bad);
        Assert.Equal(3, vm.Files.Count);

        // Folder is chosen by the user (not a default); cancelling the picker converts nothing
        dlg.Folder = null;
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        Assert.Equal(1, dlg.FolderPrompts);
        Assert.All(vm.Files, f => Assert.NotEqual(FileStatus.Done, f.Status));
        Assert.False(Directory.Exists(outDir));

        dlg.Folder = outDir;
        int histBefore = vm.History.Count(h => h.Success);
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        Assert.Equal(2, dlg.FolderPrompts);
        Assert.Equal("success", vm.BannerKind);
        Assert.All(vm.Files, f => Assert.Equal(FileStatus.Done, f.Status));
        Assert.Equal(new[] { "01-simple-array.xlsx", "02-his-patients-nested.xlsx", "04-multiple-arrays-choose.xlsx" },
            Directory.GetFiles(outDir, "*.xlsx").Select(Path.GetFileName).OrderBy(x => x).ToArray());
        Assert.Equal(3, vm.History.Count(h => h.Success) - histBefore);
        Shot(win, "06-converted");

        vm.ClearFilesCommand.Execute(null);
        Assert.Empty(vm.Files);
        Assert.False(vm.HasPreview);
        Directory.Delete(outDir, true);
    }

    [AvaloniaFact]
    public void Files_CsvBatch_UsesSettings()
    {
        var (win, vm, dlg) = Create();
        string outDir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        dlg.Folder = outDir;
        vm.IsExcel = false;
        vm.DelimiterIndex = 1;
        Pump(vm.AddPathsAsync(new[] { Path.Combine(Samples, "05-unicode-multilingual.json") }));
        Settle(vm);
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        var csv = Path.Combine(outDir, "05-unicode-multilingual.csv");
        Assert.True(File.Exists(csv));
        Assert.Contains("नमस्ते;कोच्चि", File.ReadAllText(csv));
        vm.ClearFilesCommand.Execute(null);
        Directory.Delete(outDir, true);
    }
}
