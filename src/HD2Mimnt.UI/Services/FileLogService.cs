using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.UI.Services;

public interface IFileLogService
{
    string LogFilePath { get; }
    Task<IReadOnlyList<string>> ReadRecentAsync(int maxLines, CancellationToken cancellationToken = default);
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public sealed class FileLogService : IFileLogService
{
    public string LogFilePath { get; }

    public FileLogService(string appDataPath)
    {
        var logDirectory = Path.Combine(appDataPath, "Logs");
        Directory.CreateDirectory(logDirectory);
        LogFilePath = Path.Combine(logDirectory, "application.log");
    }

    public Task<IReadOnlyList<string>> ReadRecentAsync(int maxLines, CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            if (!File.Exists(LogFilePath)) return Array.Empty<string>();
            var lines = new Queue<string>(maxLines);
            foreach (var line in File.ReadLines(LogFilePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lines.Count == maxLines) lines.Dequeue();
                lines.Enqueue(line);
            }

            return lines.ToArray();
        }, cancellationToken);
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LogFilePath))
        {
            await File.WriteAllTextAsync(destinationPath, string.Empty, cancellationToken);
            return;
        }

        await using var source = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }
}

public sealed class FileLogProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _gate = new();

    public FileLogProvider(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new FileLogger(category, _path, _gate));

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _path;
        private readonly object _gate;

        public FileLogger(string category, string path, object gate)
        {
            _category = category;
            _path = path;
            _gate = gate;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception != null) message = $"{message} | {exception}";
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_category}: {message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(_path, line);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
