using System.Security.Cryptography;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.SoundFix;

/// <summary>逐模组提交；替换文件以断开已部署硬链接，失败恢复，备份位于应用数据目录。</summary>
public sealed class SoundFixTransaction : IDisposable
{
    private sealed record Entry(string RelativePath, string BeforeHash, string AfterHash, long BeforeSize, long AfterSize);
    private sealed record Manifest(Guid ModId, string ModPath, bool IsRestore, Entry[] Files);
    private readonly Guid _modId;
    private readonly string _root;
    private readonly List<(Entry Entry, string Temporary)> _files = [];
    private readonly List<Entry> _applied = [];
    private bool _committed;
    private string? _restoreMarker;
    public string BackupPath { get; }
    public bool HasChanges => _files.Count > 0;
    public long SizeDelta => _files.Sum(file => file.Entry.AfterSize - file.Entry.BeforeSize);

    public SoundFixTransaction(Guid modId, string modPath, string backupRoot)
    {
        _modId = modId;
        _root = Path.GetFullPath(modPath);
        BackupPath = Path.Combine(backupRoot, modId.ToString("N"), DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
    }

    public async Task StageAsync(string original, byte[] data, string expectedHash, CancellationToken token)
    {
        var relative = Path.GetRelativePath(_root, original);
        _ = Resolve(_root, relative);
        var temporary = Path.Combine(Path.GetDirectoryName(original)!, $".soundfix-{Guid.NewGuid():N}.tmp");
        var entry = new Entry(relative, expectedHash, Convert.ToHexString(SHA256.HashData(data)), new FileInfo(original).Length, data.LongLength);
        _files.Add((entry, temporary));
        await File.WriteAllBytesAsync(temporary, data, token);
    }

    public async Task ApplyAsync(CancellationToken token)
    {
        if (!HasChanges) return;
        Directory.CreateDirectory(BackupPath);
        foreach (var (entry, _) in _files)
        {
            token.ThrowIfCancellationRequested();
            var original = Resolve(_root, entry.RelativePath);
            if (await HashAsync(original, token) != entry.BeforeHash) throw new InvalidDataException("模组文件已改变，请重新扫描。");
            var backup = Resolve(BackupPath, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await CopyAsync(original, backup, token);
            if (await HashAsync(backup, token) != entry.BeforeHash) throw new InvalidDataException("修复备份校验失败。");
        }
        await File.WriteAllTextAsync(Path.Combine(BackupPath, "manifest.json"),
            JsonSerializer.Serialize(new Manifest(_modId, _root, _restoreMarker is not null, _files.Select(f => f.Entry).ToArray())), token);
        foreach (var (entry, temporary) in _files)
        {
            token.ThrowIfCancellationRequested();
            var original = Resolve(_root, entry.RelativePath);
            if (await HashAsync(original, token) != entry.BeforeHash) throw new InvalidDataException("模组文件在提交期间改变。");
            File.Move(temporary, original, true);
            _applied.Add(entry);
        }
        // 这些标记属于事务；数据库保存失败时 RollbackAsync 同时撤销。
        await File.WriteAllTextAsync(Path.Combine(BackupPath, "complete"), "", CancellationToken.None);
        if (_restoreMarker is not null) await File.WriteAllTextAsync(_restoreMarker, "", CancellationToken.None);
    }

    public void Complete() => _committed = true;

    public async Task RollbackAsync()
    {
        var failures = new List<Exception>();
        foreach (var entry in _applied.AsEnumerable().Reverse())
        {
            var original = Resolve(_root, entry.RelativePath);
            var temporary = original + ".soundfix-rollback-" + Guid.NewGuid().ToString("N");
            try
            {
                await CopyAsync(Resolve(BackupPath, entry.RelativePath), temporary, CancellationToken.None);
                File.Move(temporary, original, true);
            }
            catch (Exception ex) { failures.Add(ex); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (failures.Count > 0) throw new AggregateException($"音效修复回滚失败，请使用备份：{BackupPath}", failures);
        _applied.Clear();
        if (File.Exists(Path.Combine(BackupPath, "complete"))) File.Delete(Path.Combine(BackupPath, "complete"));
        if (_restoreMarker is not null && File.Exists(_restoreMarker)) File.Delete(_restoreMarker);
    }

    public static bool HasBackup(Guid modId, string backupRoot) => FindLatest(modId, backupRoot) is not null;

    public static async Task<SoundFixTransaction> PrepareRestoreAsync(Guid modId, string modPath, string backupRoot, CancellationToken token)
    {
        var backup = FindLatest(modId, backupRoot) ?? throw new InvalidOperationException("没有可恢复的音效修复备份。");
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json"), token))
            ?? throw new InvalidDataException("音效备份记录无效。");
        var root = Path.GetFullPath(modPath);
        // 模组库可搬迁；以当前 Repository 路径、Mod 身份及逐文件哈希绑定备份。
        if (manifest.ModId != modId)
            throw new InvalidDataException("备份与当前模组身份不匹配。");
        var transaction = new SoundFixTransaction(modId, modPath, backupRoot) { _restoreMarker = Path.Combine(backup, "restored") };
        try
        {
            foreach (var entry in manifest.Files)
            {
                var original = Resolve(root, entry.RelativePath);
                if (await HashAsync(original, token) != entry.AfterHash) throw new InvalidDataException("修复后模组已更新，请勿用旧备份覆盖新内容。");
                var data = await File.ReadAllBytesAsync(Resolve(backup, entry.RelativePath), token);
                if (Convert.ToHexString(SHA256.HashData(data)) != entry.BeforeHash) throw new InvalidDataException("音效备份校验失败。");
                await transaction.StageAsync(original, data, entry.AfterHash, token);
            }
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }

    private static string? FindLatest(Guid modId, string backupRoot)
    {
        var root = Path.Combine(backupRoot, modId.ToString("N"));
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal).FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "complete")) && !File.Exists(Path.Combine(path, "restored"))
            && File.Exists(Path.Combine(path, "manifest.json"))
            && JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(path, "manifest.json"))) is { IsRestore: false });
    }

    private static string Resolve(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音效备份路径越界。");
        return path;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
    private static async Task CopyAsync(string from, string to, CancellationToken token)
    {
        await using var input = File.OpenRead(from);
        await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, token);
    }

    public void Dispose()
    {
        foreach (var (_, temporary) in _files) if (File.Exists(temporary)) File.Delete(temporary);
        if (!_committed && _applied.Count == 0 && Directory.Exists(BackupPath)) Directory.Delete(BackupPath, true);
    }
}
