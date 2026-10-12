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
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly HD2MimntDbContext _dbContext;
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(
        string appDataPath,
        HD2MimntDbContext dbContext,
        SettingsService settingsService)
    {
        _appDataPath = appDataPath;
        _databasePath = dbContext.DatabasePath;
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

            var connection = await _dbContext.GetConnectionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                var escapedPath = temporaryDatabasePath.Replace("'", "''", StringComparison.Ordinal);
                command.CommandText = $"VACUUM INTO '{escapedPath}'";
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

            // 在覆盖当前数据前完成旧备份设置的迁移及 JSON 校验。
            // 恢复无 JSON 的新备份时，不得导入当前目录里残留的旧设置。
            _ = new SettingsService(new SettingsRepository(temporaryDatabasePath), temporaryDirectory);
            await _settingsService.ReloadAfterRestoreAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _dbContext.Dispose();
                File.Copy(temporaryDatabasePath, _databasePath, overwrite: true);
                DeleteIfExists(_databasePath + "-wal");
                DeleteIfExists(_databasePath + "-shm");
                await _dbContext.InitializeDatabaseAsync(cancellationToken);
            });
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
