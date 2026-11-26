using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Services;

/// <summary>恢复写入数据库的失败时机：当前数据必须保留，且恢复前快照可回退。</summary>
[Trait("Category", "Slow")]
public sealed class SqlRestoreTimingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntRestoreTiming",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restore_WhileTargetCannotBeWritten_KeepsCurrentDataAndSafetySnapshot()
    {
        Directory.CreateDirectory(_root);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync("Test.Value", "before");
        var service = new BackupService(_root, db, settings);
        var backup = await service.CreateBackupAsync();
        await settings.SetSettingAsync("Test.Value", "after");

        using var lockConnection = await db.OpenConnectionAsync();
        using var transaction = lockConnection.BeginTransaction();
        using (var command = lockConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('Lock.Probe', '\"1\"')";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<IOException>(() => service.RestoreBackupAsync(backup.FullPath));
        transaction.Rollback();

        Assert.Contains("恢复前快照保留在", exception.Message);
        Assert.Equal("after", await settings.GetSettingAsync("Test.Value", "missing"));
        var snapshots = Directory.GetFiles(Path.Combine(_root, "Backups"), "pre-restore-*.db");
        Assert.Single(snapshots);
        Assert.True(await DatabaseIntegrityGuard.IsHealthyAsync(snapshots[0], CancellationToken.None));
    }

    [Fact]
    public async Task Restore_WithCanceledToken_DoesNotTouchDatabaseOrCreateSnapshot()
    {
        Directory.CreateDirectory(_root);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync("Test.Value", "before");
        var service = new BackupService(_root, db, settings);
        var backup = await service.CreateBackupAsync();
        await settings.SetSettingAsync("Test.Value", "after");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreBackupAsync(backup.FullPath, canceled.Token));

        Assert.Equal("after", await settings.GetSettingAsync("Test.Value", "missing"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Backups"), "pre-restore-*.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
