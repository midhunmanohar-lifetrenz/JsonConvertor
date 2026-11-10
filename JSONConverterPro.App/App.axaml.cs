using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JSONConverterPro.Core.Services;
using JSONConverterPro.Services;
using JSONConverterPro.ViewModels;
using JSONConverterPro.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JSONConverterPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Composition root. Also used by UI tests to get an isolated object graph.</summary>
    public static IServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var logStore = new LogStore();
        var sc = new ServiceCollection();
        sc.AddSingleton(logStore);
        sc.AddLogging(b => b.AddProvider(new FileLoggerProvider(null, logStore.Add)));
        sc.AddSingleton<FileService>();
        sc.AddSingleton<JsonParserService>();
        sc.AddSingleton<JsonSchemaService>();
        sc.AddSingleton<CsvExportService>();
        sc.AddSingleton<ExcelExportService>();
        sc.AddSingleton<ConversionService>();
        sc.AddSingleton<PreviewService>();
        sc.AddSingleton<AppStateService>();
        sc.AddSingleton<DialogService>();
        sc.AddSingleton<IDialogService>(p => p.GetRequiredService<DialogService>());
        sc.AddSingleton<MainViewModel>();
        configure?.Invoke(sc);
        return sc.BuildServiceProvider();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();


        var log = Services.GetRequiredService<ILogger<App>>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "Unhandled UI exception");
            Services.GetRequiredService<MainViewModel>().ShowError("Something went wrong. Details were saved to the error log.");
            e.Handled = true;   // never crash on a bad file
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainViewModel>();
            vm.ApplyTheme();
            desktop.Exit += (_, _) => vm.Shutdown();   // pasted patient data must not outlive the session
            var window = new MainWindow { DataContext = vm };
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
