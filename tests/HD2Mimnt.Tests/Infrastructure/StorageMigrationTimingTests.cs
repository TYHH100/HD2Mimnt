using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Infrastructure;

/// <summary>迁移提交前候选库损坏：不得覆盖当前库，也不得丢失当前数据。</summary>
[Trait("Category", "Slow")]
public sealed class StorageMigrationTimingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntMigrationTiming",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Apply_CorruptCandidateAtCommit_DoesNotPublishOrDeleteCurrentDatabase()
    {
        Directory.CreateDirectory(_root);
        var bootstrap = Path.Combine(_root, "bootstrap");
        var current = new StorageLocations(
            Path.Combine(_root, "current"),
            Path.Combine(_root, "current", "mods"),
            Path.Combine(_root, "current", "cache"));
        Directory.CreateDirectory(current.AppDataPath);
        using (var db = new HD2MimntDbContext(current.DatabasePath))
        {
            await db.InitializeDatabaseAsync();
            using var connection = await db.OpenConnectionAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Settings (Key, Value) VALUES ('SqlProbe', '\"keep\"')";
            await insert.ExecuteNonQueryAsync();
        }

        var next = new StorageLocations(
            Path.Combine(_root, "next"),
            Path.Combine(_root, "next", "mods"),
            Path.Combine(_root, "next", "cache"));
        const string migrationId = "timing-candidate";
        var candidateDirectory = Path.Combine(next.TempPath, ".migration-" + migrationId);
        Directory.CreateDirectory(candidateDirectory);
        var garbage = new byte[8192];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(Path.Combine(candidateDirectory, "hd2mimnt.db"), garbage);

        var store = new StorageLocationStore(bootstrap);
        await store.SaveAsync(new StorageState(current)
        {
            Pending = next,
            MigrationId = migrationId,
            ReadyToCommit = true
        });

        var message = await new StorageMigration(store).ApplyAsync(CancellationToken.None);

        Assert.Contains("改写后的新位置数据库未通过完整性校验", message, StringComparison.Ordinal);
        Assert.False(File.Exists(next.DatabasePath));
        Assert.True(StorageLocationStore.Same(current.AppDataPath, store.State.Current.AppDataPath));
        Assert.True(StorageLocationStore.Same(current.ModsPath, store.State.Current.ModsPath));
        Assert.True(StorageLocationStore.Same(current.CachePath, store.State.Current.CachePath));
        Assert.NotNull(store.State.Pending);

        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = current.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'SqlProbe'";
        Assert.Equal("\"keep\"", (string?)await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
