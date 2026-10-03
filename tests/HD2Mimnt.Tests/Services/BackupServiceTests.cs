using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;

namespace HD2Mimnt.Tests.Services;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateListAndRestore_RestoresDatabaseAndSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        HD2MimntDbContext? dbContext = null;
        try
        {
            var databasePath = Path.Combine(root, "hd2mimnt.db");
            dbContext = new HD2MimntDbContext(databasePath);
            await dbContext.InitializeDatabaseAsync();
            var settings = new SettingsService(root);
            await settings.SetSettingAsync("Test.Value", "before");

            var connection = await dbContext.GetConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('Test.Db', 'before')";
                await command.ExecuteNonQueryAsync();
            }

            var service = new BackupService(root, dbContext, settings);
            var created = await service.CreateBackupAsync();
            Assert.True(File.Exists(created.FullPath));
            Assert.Contains(await service.ListBackupsAsync(), backup => backup.FullPath == created.FullPath);

            await settings.SetSettingAsync("Test.Value", "after");
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Settings SET Value = 'after' WHERE Key = 'Test.Db'";
                await command.ExecuteNonQueryAsync();
            }

            await service.RestoreBackupAsync(created.FullPath);

            Assert.Equal("before", await settings.GetSettingAsync("Test.Value", "missing"));
            var restoredConnection = await dbContext.GetConnectionAsync();
            await using var verify = restoredConnection.CreateCommand();
            verify.CommandText = "SELECT Value FROM Settings WHERE Key = 'Test.Db'";
            Assert.Equal("before", (string?)await verify.ExecuteScalarAsync());
        }
        finally
        {
            dbContext?.Dispose();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DeleteBackup_RejectsPathOutsideBackupDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbContext = new HD2MimntDbContext(Path.Combine(root, "hd2mimnt.db"));
            await dbContext.InitializeDatabaseAsync();
            var service = new BackupService(root, dbContext, new SettingsService(root));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteBackupAsync(Path.Combine(root, "outside.hd2backup")));
            dbContext.Dispose();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
