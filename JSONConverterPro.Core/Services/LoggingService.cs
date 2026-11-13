using System.Text;
using Microsoft.Extensions.Logging;

namespace JSONConverterPro.Core.Services;

/// <summary>Minimal rolling-by-day file logger plus an optional in-memory sink for the UI error log.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Action<LogLevel, string, string>? _sink;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JSONConverterPro", "logs");

    public FileLoggerProvider(string? directory = null, Action<LogLevel, string, string>? sink = null)
    {
        _dir = directory ?? DefaultDirectory;
        _sink = sink;
        try
        {
            Directory.CreateDirectory(_dir);
            // Keep 30 days of logs.
            foreach (var old in Directory.EnumerateFiles(_dir, "app-*.log"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-30)) File.Delete(old);
        }
        catch { /* logging must never crash the app */ }
    }

    public string LogDirectory => _dir;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(LogLevel level, string category, string message)
    {
        try
        {
            string file = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_lock)
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* ignore */ }
        try { _sink?.Invoke(level, category, message); } catch { /* ignore */ }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p; private readonly string _c;
        public FileLogger(FileLoggerProvider p, string c) { _p = p; _c = c; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string msg = formatter(state, exception);
            if (exception != null) msg += Environment.NewLine + exception;
            _p.Write(logLevel, _c, msg);
        }
    }
}
