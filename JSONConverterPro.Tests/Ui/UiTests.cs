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

    private static void Trace(string m) => File.AppendAllText(Path.Combine(Path.GetTempPath(), "jcp-ui-trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {m}\n");

    private static void Shot(Window w, string name)
    {
        Trace("shot " + name);
        if (Environment.GetEnvironmentVariable("JCP_NOSHOT") == "1") return;
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ShotDir, name + ".png"));
        Trace("shot done " + name);
    }

    // One window is shared by all UI tests (constructing a second window in the same headless session stalls);
    // every test still gets its own fresh object graph and view model.
    private static MainWindow? _shared;

    private static (MainWindow win, MainViewModel vm) Create()
    {
        var sp = App.BuildServices();
        var vm = sp.GetRequiredService<MainViewModel>();
        if (_shared == null)
        {
            _shared = new MainWindow { Width = 1180, Height = 780 };
            _shared.Show();
        }
        _shared.DataContext = vm;
        sp.GetRequiredService<DialogService>().Owner = _shared;
        Dispatcher.UIThread.RunJobs();
        return (_shared, vm);
    }

    [AvaloniaFact]
    public void AddFiles_Preview_Convert_EndToEnd()
    {
        var (win, vm) = Create();
        string outDir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        vm.OutputDirectory = outDir;
        Shot(win, "01-empty");

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

        // Preview of the first (selected) file
        vm.CurrentPage = "Preview";
        Settle(vm);
        Shot(win, "03-preview");
        Assert.True(vm.HasPreview);
        Assert.Equal(2, vm.PreviewRows.Count);
        Assert.Equal("patientId", vm.PreviewColumns[0]);   // smart ordering puts ID first
        var grid = win.FindControl<DataGrid>("PreviewGrid")!;
        Assert.Equal(vm.PreviewColumns.Count, grid.Columns.Count);

        // Switching files must clear old records
        vm.SelectFile(byName["01-simple-array.json"]);
        Assert.False(vm.HasPreview);
        Settle(vm);
        Assert.Equal(3, vm.PreviewRows.Count);
        Assert.DoesNotContain("patientId", vm.PreviewColumns);

        // Multiple-array file requires a choice
        vm.SelectFile(byName["04-multiple-arrays-choose.json"]);
        Settle(vm);
        Shot(win, "04-choose-array");
        Assert.True(vm.ShowCandidates);
        Assert.False(vm.HasPreview);
        vm.SelectedCandidate = vm.Candidates.First(c => c.Path == "appointments");
        Settle(vm);
        Assert.Equal(3, vm.PreviewRows.Count);
        Assert.Equal(FileStatus.Ready, byName["04-multiple-arrays-choose.json"].Status);

        // Settings page
        vm.CurrentPage = "Settings"; Settle(vm);
        Shot(win, "05-settings");

        // Remove the bad files, convert the rest to Excel
        foreach (var bad in vm.Files.Where(f => f.Status == FileStatus.Invalid).ToList()) vm.RemoveFile(bad);
        Assert.Equal(3, vm.Files.Count);
        vm.CurrentPage = "Files";
        int histBefore = vm.History.Count(h => h.Success);
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        Dispatcher.UIThread.RunJobs();
        Shot(win, "06-converted");

        Assert.True(vm.ShowBanner);
        Assert.Equal("success", vm.BannerKind);
        Assert.All(vm.Files, f => Assert.Equal(FileStatus.Done, f.Status));
        var outputs = Directory.GetFiles(outDir, "*.xlsx").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "01-simple-array.xlsx", "02-his-patients-nested.xlsx", "04-multiple-arrays-choose.xlsx" }, outputs);
        Assert.Equal(3, vm.History.Count(h => h.Success) - histBefore);

        vm.CurrentPage = "History"; Shot(win, "07-history");
        vm.ClearFilesCommand.Execute(null);
        Assert.Empty(vm.Files);
        Assert.False(vm.HasPreview);
        // window intentionally left open: closing the last window ends the headless session
        Directory.Delete(outDir, true);
    }

    [AvaloniaFact]
    public void Csv_Conversion_FromViewModel()
    {
        var (win, vm) = Create();
        string outDir = Path.Combine(Path.GetTempPath(), "jcp-ui-" + Guid.NewGuid().ToString("N")[..6]);
        vm.OutputDirectory = outDir;
        vm.IsExcel = false;
        vm.DelimiterIndex = 1;
        Pump(vm.AddPathsAsync(new[] { Path.Combine(Samples, "05-unicode-multilingual.json") }));
        Settle(vm);
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        var csv = Path.Combine(outDir, "05-unicode-multilingual.csv");
        Assert.True(File.Exists(csv));
        Assert.Contains("नमस्ते;कोच्चि", File.ReadAllText(csv));
        vm.ClearFilesCommand.Execute(null);

        // Pasted JSON: valid, invalid, converted, and temp file removed with the entry
        Pump(vm.AddPastedJsonAsync("""{"status":"ok","data":[{"id":"A1","name":"Zoë","n":{"x":1}},{"id":"A2","name":"Bo"}]}"""));
        Pump(vm.AddPastedJsonAsync("""[{"id":1},{"id":2,]"""));
        Settle(vm);
        Assert.Equal(2, vm.Files.Count);
        var pasted = vm.Files[0]; var badPaste = vm.Files[1];
        Assert.Equal("Pasted JSON 1", pasted.Name);
        Assert.Equal(FileStatus.Ready, pasted.Status);
        Assert.Equal("2 records", pasted.RecordsText);
        Assert.Equal(FileStatus.Invalid, badPaste.Status);
        Assert.Contains("not valid JSON", badPaste.Detail);
        Assert.True(File.Exists(pasted.Path));
        vm.RemoveFile(badPaste);
        Pump(vm.ConvertCommand.ExecuteAsync(null));
        Assert.True(File.Exists(Path.Combine(outDir, "Pasted JSON 1.csv")));
        Assert.Contains("A1;Zoë", File.ReadAllText(Path.Combine(outDir, "Pasted JSON 1.csv")).Replace("\r", ""));
        string tmp = pasted.Path;
        vm.RemoveFile(pasted);
        Assert.False(File.Exists(tmp));
        Directory.Delete(outDir, true);
    }
}
