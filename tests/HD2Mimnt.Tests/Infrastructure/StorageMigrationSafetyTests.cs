using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Infrastructure;

/// <summary>
/// 存储迁移提交后，只有新位置数据库通过完整性校验才允许删除旧文件。
/// </summary>
[Trait("Category", "Slow")]
public sealed class StorageMigrationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntMigrationSafety",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupCommitted_KeepsOldFiles_WhenNewDatabaseIsCorrupt()
    {
        var (store, oldDatabase) = await CreateCommittedStateAsync(corruptNewDatabase: true);

        var message = await new StorageMigration(store).CleanupCommittedAsync();

        Assert.Contains("未通过完整性校验", message, StringComparison.Ordinal);
        Assert.True(File.Exists(oldDatabase));
    }

    [Fact]
    public async Task CleanupCommitted_DeletesOldFiles_WhenNewDatabaseIsHealthy()
    {
        var (store, oldDatabase) = await CreateCommittedStateAsync(corruptNewDatabase: false);

        var message = await new StorageMigration(store).CleanupCommittedAsync();

        Assert.Null(message);
        Assert.False(File.Exists(oldDatabase));
    }

    private async Task<(StorageLocationStore Store, string OldDatabase)> CreateCommittedStateAsync(
        bool corruptNewDatabase)
    {
        Directory.CreateDirectory(_root);
        var bootstrap = Path.Combine(_root, "bootstrap");
        var current = new StorageLocations(
            Path.Combine(_root, "current"),
            Path.Combine(_root, "current", "mods"),
            Path.Combine(_root, "current", "cache"));
        Directory.CreateDirectory(current.AppDataPath);

        var oldDatabase = Path.Combine(_root, "old", "hd2mimnt.db");
        Directory.CreateDirectory(Path.GetDirectoryName(oldDatabase)!);
        await File.WriteAllTextAsync(oldDatabase, "old database placeholder");

        if (corruptNewDatabase)
        {
            var garbage = new byte[8192];
            Random.Shared.NextBytes(garbage);
            await File.WriteAllBytesAsync(current.DatabasePath, garbage);
        }
        else
        {
            using var db = new HD2MimntDbContext(current.DatabasePath);
            await db.InitializeDatabaseAsync();
        }
        SqliteConnection.ClearAllPools();

        var store = new StorageLocationStore(bootstrap);
        await store.SaveAsync(new StorageState(current)
        {
            MigrationId = "migration-test",
            CleanupFiles = [oldDatabase]
        });
        return (store, oldDatabase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
