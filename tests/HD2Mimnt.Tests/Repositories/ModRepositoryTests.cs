using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Repositories;

public class ModRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"test_mods_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task InitializeDatabase_ShouldUpgradeLegacyModsTableAndReadEmptyFingerprint()
    {
        var id = Guid.NewGuid();
        using (var legacy = new HD2MimntDbContext(_dbPath))
        {
            await legacy.InitializeDatabaseAsync();
            // 模拟引入判重指纹之前的数据库。
            using var connection = await legacy.OpenConnectionAsync();
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE Mods DROP COLUMN ContentFingerprint";
            await drop.ExecuteNonQueryAsync();
        }

        using var upgraded = new HD2MimntDbContext(_dbPath);
        await upgraded.InitializeDatabaseAsync();
        var repository = new ModRepository(upgraded);
        await repository.AddAsync(new Mod
        {
            Id = id, Name = "Legacy", Version = "1.0", Path = Path.Combine(Path.GetTempPath(), "legacy"),
            ImportedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow
        });

        Assert.Equal(string.Empty, (await repository.GetByIdAsync(id))!.ContentFingerprint);

        await repository.SetContentFingerprintAsync(id, "abc");
        Assert.Equal("abc", (await repository.GetByIdAsync(id))!.ContentFingerprint);
    }

    [Fact]
    public async Task SetContentFingerprint_ShouldNotOverwriteOtherColumns()
    {
        using var context = new HD2MimntDbContext(_dbPath);
        await context.InitializeDatabaseAsync();
        var repository = new ModRepository(context);
        var id = Guid.NewGuid();
        await repository.AddAsync(new Mod
        {
            Id = id, Name = "Keep", Author = "Author", Version = "2.0",
            Path = Path.Combine(Path.GetTempPath(), "keep"),
            ImportedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow
        });

        await repository.SetContentFingerprintAsync(id, "fingerprint");

        var mod = (await repository.GetByIdAsync(id))!;
        Assert.Equal("fingerprint", mod.ContentFingerprint);
        Assert.Equal("Keep", mod.Name);
        Assert.Equal("Author", mod.Author);
        Assert.Equal("2.0", mod.Version);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
        try { File.Delete(_dbPath + "-wal"); } catch (IOException) { }
        try { File.Delete(_dbPath + "-shm"); } catch (IOException) { }
    }
}
