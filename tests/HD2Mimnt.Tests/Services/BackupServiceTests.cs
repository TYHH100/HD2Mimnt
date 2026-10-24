using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
using System.IO.Compression;
using System.Text.Json;

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

            using var connection = await dbContext.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('Test.Db', '\"before\"')";
                await command.ExecuteNonQueryAsync();
            }

            var service = new BackupService(root, dbContext, settings);
            var created = await service.CreateBackupAsync();
            Assert.True(File.Exists(created.FullPath));
            using (var archive = ZipFile.OpenRead(created.FullPath))
            {
                Assert.NotNull(archive.GetEntry("hd2mimnt.db"));
                Assert.Null(archive.GetEntry("settings.json"));
            }
            Assert.Contains(await service.ListBackupsAsync(), backup => backup.FullPath == created.FullPath);

            await settings.SetSettingAsync("Test.Value", "after");
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Settings SET Value = '\"after\"' WHERE Key = 'Test.Db'";
                await command.ExecuteNonQueryAsync();
            }

            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"Test.Value\":\"stale\",\"Test.Extra\":true}");
            await service.RestoreBackupAsync(created.FullPath);

            Assert.Equal("before", await settings.GetSettingAsync("Test.Value", "missing"));
            using var restoredConnection = await dbContext.OpenConnectionAsync();
            await using var verify = restoredConnection.CreateCommand();
            verify.CommandText = "SELECT Value FROM Settings WHERE Key = 'Test.Db'";
            Assert.Equal("\"before\"", (string?)await verify.ExecuteScalarAsync());
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
            Assert.False(await settings.GetSettingAsync("Test.Extra", false));
        }
        finally
        {
            dbContext?.Dispose();
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreBackupAsync_LegacyBackup_ShouldImportOnlyItsOwnSettings(bool includeJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = new HD2MimntDbContext(Path.Combine(root, "hd2mimnt.db"));
        try
        {
            await db.InitializeDatabaseAsync();
            var settings = new SettingsService(root);
            await settings.SetSettingAsync(SettingKeys.Language, "en-US");
            var service = new BackupService(root, db, settings);
            var legacyPath = Path.Combine(root, "legacy.db");
            using (var legacyDb = new HD2MimntDbContext(legacyPath))
                await legacyDb.InitializeDatabaseAsync();
            var backupPath = Path.Combine(root, "Backups", "legacy.hd2backup");
            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(legacyPath, "hd2mimnt.db");
                if (includeJson)
                {
                    using var writer = new StreamWriter(archive.CreateEntry("settings.json").Open());
                    await writer.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object>
                    { [SettingKeys.Language] = "zh-TW", [SettingKeys.DownloadParallelSegments] = 8 }));
                }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"UI.Language\":\"zh-CN\"}");

            await service.RestoreBackupAsync(backupPath);

            Assert.Equal(includeJson ? "zh-TW" : "auto", settings.GetSetting(SettingKeys.Language, "auto"));
            Assert.Equal(includeJson ? 8 : 4, new SettingsService(root).GetSetting(SettingKeys.DownloadParallelSegments, 4));
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), path => Path.GetFileName(path).StartsWith(".restore-"));
        }
        finally { db.Dispose(); TryDelete(root); }
    }

    [Fact]
    public async Task RestoreBackupAsync_CorruptLegacySettings_ShouldLeaveCurrentDatabaseAndMemoryIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = new HD2MimntDbContext(Path.Combine(root, "hd2mimnt.db"));
        try
        {
            await db.InitializeDatabaseAsync();
            var settings = new SettingsService(root);
            await settings.SetSettingAsync(SettingKeys.Language, "en-US");
            var service = new BackupService(root, db, settings);
            var legacyPath = Path.Combine(root, "legacy.db");
            using (var legacyDb = new HD2MimntDbContext(legacyPath))
                await legacyDb.InitializeDatabaseAsync();
            var backupPath = Path.Combine(root, "Backups", "invalid.hd2backup");
            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(legacyPath, "hd2mimnt.db");
                using var writer = new StreamWriter(archive.CreateEntry("settings.json").Open());
                await writer.WriteAsync("{broken");
            }

            await Assert.ThrowsAsync<JsonException>(() => service.RestoreBackupAsync(backupPath));

            Assert.Equal("en-US", settings.GetSetting(SettingKeys.Language, "auto"));
            Assert.Equal("en-US", new SettingsService(root).GetSetting(SettingKeys.Language, "auto"));
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), path => Path.GetFileName(path).StartsWith(".restore-"));
        }
        finally { db.Dispose(); TryDelete(root); }
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
