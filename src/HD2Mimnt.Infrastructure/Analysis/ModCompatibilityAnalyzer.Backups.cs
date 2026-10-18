using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class ModCompatibilityAnalyzer
{
    private sealed class BackupMetadata
    {
        public bool ExistedBefore { get; init; } = true;
        public string Sha256 { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
    }

    private static async Task<CompatibilityRepairResult> CommitPreparedAsync(IReadOnlyList<PreparedFile> files, int actionCount, CancellationToken token)
    {
        if (files.Count == 0) return new CompatibilityRepairResult { Success = true };
        var committed = new List<(PreparedFile File, string Backup)>();
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(file.Original) != (file.ExpectedHash is not null)
                    || file.ExpectedHash is not null && await ShaAsync(file.Original, token) != file.ExpectedHash)
                    throw new InvalidDataException($"目标在准备后发生变化：{file.Original}");
            }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var backup = CreateBackupPath(new FileInfo(file.Original));
                var exists = file.ExpectedHash is not null;
                if (file.Temporary.Length == 0)
                {
                    if (!exists) continue;
                    File.Move(file.Original, backup);
                }
                else if (exists) File.Replace(file.Temporary, file.Original, backup, true);
                else
                {
                    // 空备份加 sidecar 表示修复前文件不存在，使时间点恢复也能撤销新建伴随文件。
                    await File.WriteAllBytesAsync(backup, [], token);
                    File.Move(file.Temporary, file.Original);
                }
                committed.Add((file, backup));
                var metadata = new BackupMetadata { ExistedBefore = exists,
                    Sha256 = file.ExpectedHash ?? Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), CreatedAtUtc = DateTime.UtcNow };
                await File.WriteAllTextAsync(backup + ".json", JsonSerializer.Serialize(metadata), CancellationToken.None);
            }
            return new CompatibilityRepairResult { Success = true, Changed = committed.Count > 0,
                AppliedActionCount = actionCount, BackupPaths = committed.Select(c => c.Backup).ToList(),
                RecoveredPaths = committed.Where(c => c.File.ExpectedHash is null).Select(c => c.File.Original).ToList() };
        }
        catch (Exception ex)
        {
            var errors = new List<string>();
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.File.ExpectedHash is null) File.Delete(item.File.Original);
                    else File.Copy(item.Backup, item.File.Original, true);
                }
                catch (Exception restoreError) { errors.Add($"{item.File.Original}: {restoreError.Message}"); }
            }
            if (ex is OperationCanceledException && errors.Count == 0) throw;
            return new CompatibilityRepairResult { ErrorMessage = ex.Message, Changed = errors.Count > 0,
                RecoveryErrors = errors, BackupPaths = committed.Select(c => c.Backup).ToList() };
        }
    }

    public Task<IReadOnlyList<CompatibilityBackupEntry>> GetBackupHistoryAsync(string modPath, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CompatibilityBackupEntry>>(async () =>
        {
            var entries = new List<CompatibilityBackupEntry>();
            foreach (var backup in await GetBackupsAsync(modPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = BackupOriginal(backup)!;
                var info = new FileInfo(backup);
                var metadata = await ReadBackupMetadataAsync(backup, cancellationToken);
                var hash = await ShaAsync(backup, cancellationToken);
                var error = metadata is not null && metadata.Sha256 != hash ? "备份校验和不匹配。" : null;
                entries.Add(new CompatibilityBackupEntry { BackupPath = backup, OriginalPath = original,
                    CreatedAtUtc = metadata?.CreatedAtUtc ?? ParseBackupTime(backup), Size = info.Length, Sha256 = hash,
                    CanRestore = error is null, Error = error, CurrentMatches = metadata?.ExistedBefore == false
                        ? !File.Exists(original) : File.Exists(original) && await ShaAsync(original, cancellationToken) == hash });
            }
            return entries.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.BackupPath, StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    private static DateTime ParseBackupTime(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"\.(\d{8}-\d{6})(?:-\d+)?\.hd2mimnt-backup$");
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : File.GetLastWriteTimeUtc(path);
    }

    private static async Task<BackupMetadata?> ReadBackupMetadataAsync(string backup, CancellationToken token)
    {
        if (!File.Exists(backup + ".json")) return null;
        try
        {
            if (new FileInfo(backup + ".json").Length > 8192) throw new InvalidDataException("备份元数据过大。");
            return JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync(backup + ".json", token))
                ?? throw new InvalidDataException("备份元数据为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("备份元数据无法解析。", ex); }
    }

    private async Task<CompatibilityRepairResult> RestoreEntriesAsync(string modPath, IReadOnlyList<string> backups, CancellationToken token)
    {
        var valid = await GetBackupsAsync(modPath, token);
        var files = new List<PreparedFile>();
        using var preparation = new RepairPreparation();
        foreach (var backup in backups)
        {
            token.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(backup);
            if (!valid.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("备份不属于此模组。");
            var original = BackupOriginal(fullPath)!;
            var metadata = await ReadBackupMetadataAsync(fullPath, token);
            if (metadata is not null && metadata.Sha256 != await ShaAsync(fullPath, token))
                throw new InvalidDataException("备份校验和不匹配。");
            var currentHash = File.Exists(original) ? await ShaAsync(original, token) : null;
            if (metadata?.ExistedBefore == false)
            { if (currentHash is not null) files.Add(new PreparedFile(original, string.Empty, currentHash)); continue; }
            var temporary = Path.Combine(Path.GetDirectoryName(original)!, $".restore-{Guid.NewGuid():N}.tmp");
            preparation.Temporaries.Add(temporary);
            await CopyAsync(fullPath, temporary, token);
            files.Add(new PreparedFile(original, temporary, currentHash));
        }
        return await CommitPreparedAsync(files, files.Count, token);
    }

    public Task<CompatibilityRepairResult> RollbackToAsync(string modPath, DateTime targetUtc, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            if (targetUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("回滚时间必须为 UTC。", nameof(targetUtc));
            await RepairGate.WaitAsync(cancellationToken);
            try
            {
                var history = await GetBackupHistoryAsync(modPath, cancellationToken);
                var selected = history.Where(e => e.CreatedAtUtc <= targetUtc && e.CanRestore).GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.BackupPath, StringComparer.Ordinal).First().BackupPath).ToArray();
                return await RestoreEntriesAsync(modPath, selected, cancellationToken);
            }
            finally { RepairGate.Release(); }
        }, cancellationToken);

    public Task<int> DeleteBackupAsync(string modPath, string backupPath, CancellationToken cancellationToken = default)
        => DeleteBackupsCoreAsync(modPath, [Path.GetFullPath(backupPath)], cancellationToken);

    private Task<int> DeleteBackupsCoreAsync(string modPath, IReadOnlyList<string> paths, CancellationToken token)
        => Task.Run(async () =>
        {
            await RepairGate.WaitAsync(token);
            try
            {
                var valid = await GetBackupsAsync(modPath, token);
                if (paths.Any(path => !valid.Contains(path, StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException("备份不属于此模组。");
                var count = 0;
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    File.Delete(path);
                    if (File.Exists(path + ".json")) File.Delete(path + ".json");
                    count++;
                }
                return count;
            }
            finally { RepairGate.Release(); }
        }, token);

    public async Task<int> CleanOldBackupsAsync(string modPath, int keepPerFile = 3, CancellationToken cancellationToken = default)
    {
        if (keepPerFile < 0) throw new ArgumentOutOfRangeException(nameof(keepPerFile));
        var history = await GetBackupHistoryAsync(modPath, cancellationToken);
        var paths = history.GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.BackupPath, StringComparer.Ordinal).Skip(keepPerFile))
            .Select(e => e.BackupPath).ToArray();
        return await DeleteBackupsCoreAsync(modPath, paths, cancellationToken);
    }
}
