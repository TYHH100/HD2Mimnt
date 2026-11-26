using System.IO.Compression;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Creates and restores self-contained application backups.
/// Application settings are included in the SQLite snapshot; legacy JSON backups remain readable.
/// </summary>
public sealed class BackupService
{
    private const string DatabaseEntryName = "hd2mimnt.db";
    private const string SettingsEntryName = "settings.json";

    private readonly string _appDataPath;
    private readonly string _backupDirectory;
    private readonly HD2MimntDbContext _dbContext;
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event EventHandler? Restored;

    public BackupService(
        string appDataPath,
        HD2MimntDbContext dbContext,
        SettingsService settingsService)
    {
        _appDataPath = appDataPath;
        _backupDirectory = Path.Combine(appDataPath, "Backups");
        _dbContext = dbContext;
        _settingsService = settingsService;
        Directory.CreateDirectory(_backupDirectory);
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Directory.EnumerateFiles(_backupDirectory, "*.hd2backup", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new BackupInfo(file.Name, file.FullName, file.LastWriteTimeUtc, file.Length))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? temporaryDatabasePath = null;
        string? temporaryBackupPath = null;
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(_backupDirectory, $"backup-{stamp}-{Guid.NewGuid():N}.hd2backup");
            temporaryDatabasePath = Path.Combine(_backupDirectory, $".backup-{Guid.NewGuid():N}.db");
            temporaryBackupPath = backupPath + ".tmp";

            using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO @destination";
                command.Parameters.AddWithValue("@destination", temporaryDatabasePath);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var stream = new FileStream(temporaryBackupPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await AddFileAsync(archive, temporaryDatabasePath, DatabaseEntryName, cancellationToken);
            }

            File.Move(temporaryBackupPath, backupPath);
            temporaryBackupPath = null;
            var file = new FileInfo(backupPath);
            return new BackupInfo(file.Name, file.FullName, file.LastWriteTimeUtc, file.Length);
        }
        finally
        {
            DeleteIfExists(temporaryDatabasePath);
            DeleteIfExists(temporaryBackupPath);
            _gate.Release();
        }
    }

    public async Task RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? temporaryDirectory = null;
        try
        {
            ValidateBackupPath(backupPath);
            temporaryDirectory = Path.Combine(_appDataPath, $".restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var temporaryDatabasePath = Path.Combine(temporaryDirectory, DatabaseEntryName);
            var temporarySettingsPath = Path.Combine(temporaryDirectory, SettingsEntryName);

            using (var archive = ZipFile.OpenRead(backupPath))
            {
                var databaseEntry = archive.GetEntry(DatabaseEntryName)
                    ?? throw new InvalidDataException("备份缺少数据库文件。");
                databaseEntry.ExtractToFile(temporaryDatabasePath, overwrite: true);
                var settingsEntry = archive.GetEntry(SettingsEntryName);
                settingsEntry?.ExtractToFile(temporarySettingsPath, overwrite: true);
            }

            // 覆盖当前库之前先校验备份库：坏备份不能把损坏带进在线数据库。
            var restoredDatabase = new FileInfo(temporaryDatabasePath);
            if (!restoredDatabase.Exists || restoredDatabase.Length == 0 ||
                !await DatabaseIntegrityGuard.IsHealthyAsync(temporaryDatabasePath, cancellationToken))
                throw new InvalidDataException("备份数据库未通过完整性校验，已取消恢复。");

            // 在覆盖当前数据前完成旧备份设置的迁移及 JSON 校验。
            // 恢复无 JSON 的新备份时，不得导入当前目录里残留的旧设置。
            _ = new SettingsService(new SettingsRepository(temporaryDatabasePath), temporaryDirectory);

            // 覆盖在线库前保留一份当前状态快照：恢复中途失败时还能回到恢复前。
            var safetySnapshotPath = Path.Combine(_backupDirectory,
                $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            try
            {
                await CreateDatabaseSnapshotAsync(safetySnapshotPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DeleteIfExists(safetySnapshotPath);
                throw;
            }
            catch (Exception ex)
            {
                DeleteIfExists(safetySnapshotPath);
                throw new InvalidOperationException($"无法创建恢复前安全快照，已取消恢复：{ex.Message}", ex);
            }

            var restored = false;
            var databaseTouched = false;
            try
            {
                await _settingsService.ReloadAfterRestoreAsync(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    databaseTouched = true;
                    try
                    {
                        // SQLite 在线备份以事务替换内容；不关闭其它操作的连接，也不删除活动 WAL。
                        await _dbContext.ImportFromAsync(temporaryDatabasePath, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new IOException($"恢复数据库失败，当前库可能不完整；恢复前快照保留在：{safetySnapshotPath}", ex);
                    }
                    if (!await DatabaseIntegrityGuard.IsHealthyAsync(_dbContext.DatabasePath, CancellationToken.None))
                        throw new IOException($"恢复后的数据库未通过完整性校验；恢复前快照保留在：{safetySnapshotPath}");
                    await _dbContext.InitializeDatabaseAsync(CancellationToken.None);
                });
                restored = true;
                Restored?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                // 只在真正开始覆盖数据库后保留失败快照；前置校验失败时删除，避免堆积。
                if (restored || !databaseTouched) DeleteIfExists(safetySnapshotPath);
            }
        }
        finally
        {
            if (temporaryDirectory != null)
            {
                try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
            }

            _gate.Release();
        }
    }

    public async Task DeleteBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ValidateBackupPath(backupPath);
            File.Delete(backupPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateBackupPath(string backupPath)
    {
        var fullPath = Path.GetFullPath(backupPath);
        var root = Path.GetFullPath(_backupDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".hd2backup", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能操作备份目录中的 .hd2backup 文件。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("备份文件不存在。", fullPath);
        }
    }

    private async Task CreateDatabaseSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO @destination";
        command.Parameters.AddWithValue("@destination", destinationPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    private static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed record BackupInfo(string Name, string FullPath, DateTime LastWriteTimeUtc, long SizeBytes)
{
    public string DisplayDate => LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string DisplaySize => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024d:0.##} KB"
        : $"{SizeBytes / (1024d * 1024):0.##} MB";
}
