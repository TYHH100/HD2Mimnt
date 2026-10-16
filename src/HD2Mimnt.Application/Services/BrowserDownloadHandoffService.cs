using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Services;

/// <summary>One explicit browser download at a time; never reads browser credentials or deletes its files.</summary>
public sealed class BrowserDownloadHandoffService(string downloadsDirectory, TimeSpan? pollInterval = null, TimeSpan? timeout = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string DownloadsDirectory { get; } = Path.GetFullPath(downloadsDirectory);

    public async Task<BrowserDownloadSession> StartAsync(PlatformModFile file, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("已有浏览器下载正在等待，请先完成或取消。");
        try
        {
            var existing = await Task.Run(() => BrowserDownloadSession.Snapshot(DownloadsDirectory), token);
            return new(DownloadsDirectory, file, existing, pollInterval ?? TimeSpan.FromSeconds(1),
                timeout ?? TimeSpan.FromMinutes(15), () => _gate.Release());
        }
        catch { _gate.Release(); throw; }
    }

    /// <summary>Keep the source read-only and prevent writes/deletion while the existing import flow reads it.</summary>
    public static async Task UseFileAsync(string path, PlatformModFile file,
        Func<string, CancellationToken, Task> action, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        await using var lease = await Task.Run(() => OpenFile(fullPath, file, FileShare.Read), token);
        token.ThrowIfCancellationRequested();
        await action(fullPath, token);
    }

    internal static FileStream OpenFile(string path, PlatformModFile file, FileShare share)
    {
        if (!new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("请选择已下载完成的模组压缩包。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        if (stream.Length <= 0 || file.Size > 0 && stream.Length != file.Size)
        {
            stream.Dispose();
            throw new InvalidDataException("文件大小与所选附件不一致，请确认版本及下载是否完成。");
        }
        return stream;
    }
}

public sealed class BrowserDownloadSession : IDisposable
{
    private readonly string _directory;
    private readonly PlatformModFile _file;
    private readonly HashSet<string> _existing;
    private readonly Regex? _name;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly Action _release;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<string> _manual = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal BrowserDownloadSession(string directory, PlatformModFile file, HashSet<string> existing,
        TimeSpan interval, TimeSpan timeout, Action release)
    {
        _directory = directory;
        _file = new() { FileName = file.FileName, Size = file.Size };
        _existing = existing; _interval = interval; _timeout = timeout; _release = release;
        var name = Path.GetFileName(file.FileName);
        // Unknown names/sizes require the user's explicit file selection instead of guessing.
        if (!string.IsNullOrWhiteSpace(name) && file.Size > 0)
            _name = new("^" + Regex.Escape(Path.GetFileNameWithoutExtension(name)) + @"(?: \(\d+\))?" +
                Regex.Escape(Path.GetExtension(name)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static HashSet<string> Snapshot(string directory)
    {
        try { return Directory.EnumerateFiles(directory).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public async Task SelectFileAsync(string path, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var fullPath = Path.GetFullPath(path);
        // The user may rename a browser download; only automatic selection relies on its original name.
        using (await Task.Run(() => BrowserDownloadHandoffService.OpenFile(fullPath, _file, FileShare.None), token)) { }
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _manual.TrySetResult(fullPath);
    }

    public async Task<string> WaitForFileAsync(CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        cancellation.CancelAfter(_timeout);
        try
        {
            return await Task.Run(async () =>
            {
                (string Path, long Length, DateTime Modified)? previous = null;
                var stable = 0;
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_manual.Task.IsCompletedSuccessfully) return await _manual.Task;
                    var candidates = FindCandidates();
                    var candidate = candidates.Count == 1 ? candidates[0] : ((string Path, long Length, DateTime Modified)?)null;
                    stable = candidate != null && candidate == previous ? stable + 1 : 0;
                    previous = candidate;
                    if (stable >= 2 && candidate is { } ready)
                    {
                        try
                        {
                            using var lease = BrowserDownloadHandoffService.OpenFile(ready.Path, _file, FileShare.None);
                            return ready.Path;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    await Task.WhenAny(Task.Delay(_interval, cancellation.Token), _manual.Task);
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_closed.IsCancellationRequested)
        { throw new TimeoutException("等待浏览器下载超时，请重新操作或选择已下载的文件。"); }
    }

    private List<(string Path, long Length, DateTime Modified)> FindCandidates()
    {
        var result = new List<(string, long, DateTime)>();
        if (_name == null) return result;
        foreach (var path in Snapshot(_directory))
        {
            if (_existing.Contains(path) || !_name.IsMatch(Path.GetFileName(path))) continue;
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != _file.Size ||
                    new[] { ".part", ".crdownload", ".tmp", ".download", ".partial" }.Any(suffix => File.Exists(path + suffix))) continue;
                result.Add((path, file.Length, file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel();
        _closed.Dispose();
        _release();
    }
}
