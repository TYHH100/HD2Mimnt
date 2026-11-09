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
            // 模拟引入判重指纹之前的数据库：那个年代既没有该列，也没有按它建的判重索引。
            using var connection = await legacy.OpenConnectionAsync();
            using var dropIndex = connection.CreateCommand();
            dropIndex.CommandText = "DROP INDEX IF EXISTS idx_mods_content_fingerprint";
            await dropIndex.ExecuteNonQueryAsync();
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
    public async Task FindContentCandidates_ShouldReturnIdentityMatchesAndUnidentifiedRows()
    {
        using var context = new HD2MimntDbContext(_dbPath);
        await context.InitializeDatabaseAsync();
        var repository = new ModRepository(context);
        Mod Create(string name, string hash, string fingerprint) => new()
        {
            Id = Guid.NewGuid(), Name = name, Version = "1.0", Path = Path.Combine(Path.GetTempPath(), name),
            ImportedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, Hash = hash, ContentFingerprint = fingerprint
        };
        var matchedByHash = Create("A", "hash-1", "fp-1");
        var unrelated = Create("B", "hash-2", "fp-2");
        var missingHash = Create("C", "", "fp-1");
        var missingFingerprint = Create("D", "hash-1", "");
        foreach (var mod in new[] { matchedByHash, unrelated, missingHash, missingFingerprint })
            await repository.AddAsync(mod);

        var candidates = (await repository.FindContentCandidatesAsync("hash-1", "fp-1")).Select(m => m.Id).ToList();

        Assert.Contains(matchedByHash.Id, candidates);
        Assert.Contains(missingHash.Id, candidates);
        Assert.Contains(missingFingerprint.Id, candidates);
        // 身份已持久化且与本次导入无关的行不再进入判重循环。
        Assert.DoesNotContain(unrelated.Id, candidates);
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
