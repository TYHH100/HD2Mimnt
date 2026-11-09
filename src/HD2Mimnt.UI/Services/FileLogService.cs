using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.UI.Services;

public interface IFileLogService
{
    /// <summary>当天正在写入的日志文件（界面显示与“打开所在目录”用）。</summary>
    string LogFilePath { get; }

    Task<IReadOnlyList<string>> ReadRecentAsync(int maxLines, CancellationToken cancellationToken = default);
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>按当前保留天数清理过期日志分片；保留天数设置变更后可直接调用。</summary>
    void TrimExpired();
}

/// <summary>
/// 日志文件按天分片：application-yyyy-MM-dd.log。单日超过上限时再滚出
/// application-yyyy-MM-dd_HHmmss.log，保证任何时刻单个文件都不会无限增长。
/// </summary>
public sealed class FileLogService : IFileLogService
{
    private readonly string _directory;
    private readonly string _fileStem;
    private readonly Func<int> _retentionDays;

    public FileLogService(string logDirectory, Func<int>? retentionDays = null)
    {
        _directory = logDirectory;
        _fileStem = "application";
        _retentionDays = retentionDays ?? (() => 30);
        Directory.CreateDirectory(_directory);
        // 构造即做一次过期清理：升级后旧日志不会一直留着。
        TrimExpired();
    }

    public void TrimExpired() => FileLogProvider.TrimExpired(_directory, _fileStem, _retentionDays());

    public string LogFilePath => CurrentLogPath(_directory, _fileStem);

    internal static string CurrentLogPath(string directory, string fileStem) =>
        Path.Combine(directory, $"{fileStem}-{DateTime.Now:yyyy-MM-dd}.log");

    public async Task<IReadOnlyList<string>> ReadRecentAsync(int maxLines, CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0) return Array.Empty<string>();
        var collected = new List<string>();
        // 新文件在前：先读今天的，读满就不再碰更早的文件。
        foreach (var file in LogFiles().AsEnumerable().Reverse())
        {
            if (collected.Count >= maxLines) break;
            var tail = await Task.Run(() => ReadTail(file, maxLines - collected.Count, cancellationToken), cancellationToken);
            collected.InsertRange(0, tail);
        }

        return collected;
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var source in LogFiles())
        {
            if (!File.Exists(source)) continue;
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                await input.CopyToAsync(target, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个分片读取失败不影响整次导出。
            }
        }
    }

    /// <summary>按时间升序返回参与读取/导出的日志分片（含旧版本的单文件日志）。</summary>
    private IReadOnlyList<string> LogFiles()
    {
        var files = new List<string>();
        var legacy = Path.Combine(_directory, $"{_fileStem}.log");
        if (File.Exists(legacy)) files.Add(legacy);
        files.AddRange(Directory.GetFiles(_directory, $"{_fileStem}-*.log")
            .OrderBy(path => path, StringComparer.Ordinal));
        return files;
    }

    private static IReadOnlyList<string> ReadTail(string path, int maxLines, CancellationToken cancellationToken)
    {
        if (maxLines <= 0 || !File.Exists(path)) return Array.Empty<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        // 从尾部反向读取：同时限制字节数与行数。
        const int byteLimit = 8 * 1024 * 1024;
        var blocks = new List<byte[]>();
        var position = stream.Length;
        var bytesRead = 0;
        var lineBreaks = 0;
        while (position > 0 && bytesRead < byteLimit && lineBreaks <= maxLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(64 * 1024, Math.Min(position, byteLimit - bytesRead));
            position -= length;
            stream.Position = position;
            var block = new byte[length];
            var read = 0;
            while (read < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(block, read, length - read);
                if (count == 0) break;
                read += count;
            }
            if (read != length) Array.Resize(ref block, read);
            lineBreaks += block.Count(value => value == (byte)'\n');
            blocks.Add(block);
            bytesRead += length;
        }
        blocks.Reverse();
        var text = Encoding.UTF8.GetString(blocks.SelectMany(block => block).ToArray());
        if (position > 0)
        {
            var boundary = text.IndexOf('\n');
            text = boundary >= 0 ? text[(boundary + 1)..] : string.Empty;
        }
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.TakeLast(maxLines).ToArray();
    }
}

/// <summary>
/// 文件日志 Provider。与旧实现的差别：
/// 1. 格式集中在 <see cref="LogLineFormat"/>，界面的解析规则与之对称；
/// 2. 保留结构化字段（k=v）而不是只落渲染后的文本；
/// 3. 实现真正的 BeginScope，作用域字段自动附加到该作用域内的每一行；
/// 4. 常驻文件句柄 + 按天分片，替代逐行 AppendAllText 的开关文件；
/// 5. 按保留天数清理过期分片。
/// </summary>
public sealed class FileLogProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly LogFileSink _sink;
    private readonly AsyncLocal<LogScope?> _scope = new();

    /// <summary>兼容旧调用：传入完整文件路径，取目录与文件名主干。</summary>
    public FileLogProvider(string path)
        : this(Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : ".",
            Path.GetFileNameWithoutExtension(path))
    {
    }

    public FileLogProvider(string directory, string fileStem, Func<int>? retentionDays = null) =>
        _sink = new LogFileSink(directory, fileStem, retentionDays ?? (() => 30));

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new FileLogger(category, this));

    public void Dispose()
    {
        _sink.Dispose();
        _loggers.Clear();
    }

    /// <summary>删除超出保留天数的日志分片；设置变更后可立即调用。</summary>
    public static void TrimExpired(string directory, string fileStem, int retentionDays)
    {
        if (!Directory.Exists(directory)) return;
        var keep = Math.Clamp(retentionDays, 1, 3650);
        var threshold = DateOnly.FromDateTime(DateTime.Now).AddDays(-(keep - 1));
        foreach (var file in Directory.GetFiles(directory, $"{fileStem}-*.log"))
        {
            if (!TryGetFileDate(file, fileStem, out var date) || date >= threshold) continue;
            TryDelete(file);
        }

        // 旧版本的单文件日志：文件名没有日期，按最后写入时间判断。
        var legacy = Path.Combine(directory, $"{fileStem}.log");
        if (File.Exists(legacy) && File.GetLastWriteTime(legacy) < DateTime.Now.AddDays(-keep))
        {
            TryDelete(legacy);
        }
    }

    private static bool TryGetFileDate(string path, string fileStem, out DateOnly date)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = $"{fileStem}-";
        date = default;
        return name.Length >= prefix.Length + 10
               && name.StartsWith(prefix, StringComparison.Ordinal)
               && DateOnly.TryParseExact(name.Substring(prefix.Length, 10), "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private LogScope? CurrentScope => _scope.Value;
    private void SetCurrentScope(LogScope? scope) => _scope.Value = scope;

    private IReadOnlyList<KeyValuePair<string, object?>> ScopeValues()
    {
        var values = new List<KeyValuePair<string, object?>>();
        for (var scope = _scope.Value; scope is not null; scope = scope.Parent)
        {
            // 由内向外收集后再前置，保证外层作用域字段排在前面。
            values.InsertRange(0, scope.Values);
        }

        return values;
    }

    private void Write(string line) => _sink.Write(line);

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLogProvider _owner;

        public FileLogger(string category, FileLogProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var fields = LogLineFormat.ExtractFields(state);
            if (fields.Count == 0 && state is not null)
            {
                // 非结构化作用域（例如 BeginScope("扫描中")）仍保留可读文本。
                fields = [new KeyValuePair<string, object?>("scope", state.ToString())];
            }

            var scope = new LogScope(_owner, _owner.CurrentScope, fields);
            _owner.SetCurrentScope(scope);
            return scope;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception) ?? string.Empty;
            var fields = LogLineFormat.ExtractFields(state);
            // 正文里已经出现过的值不再落成字段，避免同一信息写两遍。
            fields.RemoveAll(field => LogLineFormat.IsRenderedInMessage(field, message));
            var scopeValues = _owner.ScopeValues();
            if (scopeValues.Count > 0)
            {
                // 作用域字段在前、模板参数在后：前者描述场景，后者描述本次事件。
                fields.InsertRange(0, scopeValues);
            }

            var line = LogLineFormat.BuildLine(DateTimeOffset.Now, logLevel, _category, message, fields);
            if (exception is not null)
            {
                // 异常独立成段而不挤在同一行：LogViewModel 会把非行首的续行折叠回上一条记录。
                line = line + Environment.NewLine + exception;
            }

            _owner.Write(line);
        }
    }

    private sealed class LogScope : IDisposable
    {
        private readonly FileLogProvider _owner;
        private bool _disposed;

        public LogScope(FileLogProvider owner, LogScope? parent, IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            _owner = owner;
            Parent = parent;
            Values = values;
        }

        public LogScope? Parent { get; }
        public IReadOnlyList<KeyValuePair<string, object?>> Values { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 只有栈顶作用域被释放时才回退，避免乱序 Dispose 破坏外层作用域。
            if (ReferenceEquals(_owner.CurrentScope, this)) _owner.SetCurrentScope(Parent);
        }
    }
}

/// <summary>
/// 日志写入器：按天切换文件、单日超限再滚存、常驻句柄、写入失败自动降级为重新打开。
/// 保留天数通过委托读取，设置变更后下一次检查即可生效。
/// </summary>
internal sealed class LogFileSink : IDisposable
{
    private readonly string _directory;
    private readonly string _fileStem;
    private readonly Func<int> _retentionDays;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _openedDay;

    public LogFileSink(string directory, string fileStem, Func<int> retentionDays)
    {
        _directory = directory;
        _fileStem = fileStem;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                EnsureOpen();
                _writer!.WriteLine(line);
                if (_writer.BaseStream.Length >= LogLineFormat.MaxFileBytes) SwitchFile(_openedDay);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // 日志不能替换业务异常，也不能递归调用 ILogger 记录自身的写入失败。
                System.Diagnostics.Debug.WriteLine($"无法写入日志 {_directory}: {writeError.Message}");
                CloseUnlocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) CloseUnlocked();
    }

    private void EnsureOpen()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _openedDay != today)
        {
            // 跨天：收尾当天分片，并顺手清理过期日志。
            CloseUnlocked();
            FileLogProvider.TrimExpired(_directory, _fileStem, _retentionDays());
        }

        if (_writer is not null) return;

        FileLogProvider.TrimExpired(_directory, _fileStem, _retentionDays());
        OpenUnlocked(today);
    }

    private void SwitchFile(DateOnly day)
    {
        CloseUnlocked();
        OpenUnlocked(day);
    }

    private void OpenUnlocked(DateOnly day)
    {
        var path = Path.Combine(_directory, $"{_fileStem}-{day:yyyy-MM-dd}.log");
        if (File.Exists(path) && new FileInfo(path).Length >= LogLineFormat.MaxFileBytes)
        {
            // 单日超限：同一日期再加时间戳后缀，避免覆盖当天已有内容。
            path = Path.Combine(_directory, $"{_fileStem}-{day:yyyy-MM-dd}_{DateTime.Now:HHmmss}.log");
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 8 * 1024);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _openedDay = day;
    }

    private void CloseUnlocked()
    {
        try { _writer?.Dispose(); }
        catch (IOException) { }
        _writer = null;
    }
}
