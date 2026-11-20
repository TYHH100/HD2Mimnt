using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Infrastructure;

/// <summary>SQL 安全时序：外键顺序、并发删除、数据库已提交后的文件清理失败。</summary>
public sealed class SqlSafetyTimingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntSqlSafety",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ForeignKeys_AreEnabled_OnEveryConnection()
    {
        var (db, _) = await CreateDatabaseAsync();
        using var _ = db;
        for (var i = 0; i < 3; i++)
        {
            using var connection = await db.OpenConnectionAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task DeleteMod_WithDeploymentRecord_IsBlockedUntilRecordsRemoved()
    {
        var (db, path) = await CreateDatabaseAsync();
        using var _ = db;
        var now = DateTime.UtcNow.ToString("O");
        var profileId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        using var connection = await db.OpenConnectionAsync();
        await ExecuteAsync(connection, @"
            INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES (@Profile, 'P', @Now, @Now, 0);
            INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES (@Mod, 'M', '1', @Path, @Now, @Now);
            INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES (@Profile, @Mod, 1, 0);
            INSERT INTO DeploymentRecords (Id, SessionId, ProfileId, ModId, StartTime) VALUES (@Record, @Record, @Profile, @Mod, @Now);
            INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath) VALUES (@File, @Record, @Mod, 's', 't');",
            ("@Profile", profileId.ToString()), ("@Mod", modId.ToString()), ("@Record", recordId.ToString()),
            ("@File", Guid.NewGuid().ToString()), ("@Now", now), ("@Path", path));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "DELETE FROM Mods WHERE Id = @Mod", ("@Mod", modId.ToString())));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ProfileMods WHERE ModId = @Mod",
            ("@Mod", modId.ToString())));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM DeploymentFiles WHERE ModId = @Mod",
            ("@Mod", modId.ToString())));

        await ExecuteAsync(connection, "DELETE FROM DeploymentRecords WHERE Id = @Record",
            ("@Record", recordId.ToString()));
        await ExecuteAsync(connection, "DELETE FROM Mods WHERE Id = @Mod", ("@Mod", modId.ToString()));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ProfileMods WHERE ModId = @Mod",
            ("@Mod", modId.ToString())));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM DeploymentFiles WHERE ModId = @Mod",
            ("@Mod", modId.ToString())));
    }

    [Fact]
    public async Task DeleteProfile_CascadesSeparatorsAndMembers()
    {
        var (db, path) = await CreateDatabaseAsync();
        using var _ = db;
        var now = DateTime.UtcNow.ToString("O");
        var profileId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        using var connection = await db.OpenConnectionAsync();
        await ExecuteAsync(connection, @"
            INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES (@Profile, 'P', @Now, @Now, 0);
            INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES (@Mod, 'M', '1', @Path, @Now, @Now);
            INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES (@Profile, @Mod, 1, 0);
            INSERT INTO ProfileSeparators (Id, ProfileId, Name, [Order], IsCollapsed, Color)
                VALUES (@Separator, @Profile, 'Group', 0, 0, '');",
            ("@Profile", profileId.ToString()), ("@Mod", modId.ToString()),
            ("@Separator", Guid.NewGuid().ToString()), ("@Now", now), ("@Path", path));

        await ExecuteAsync(connection, "DELETE FROM Profiles WHERE Id = @Profile",
            ("@Profile", profileId.ToString()));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ProfileMods WHERE ProfileId = @Profile",
            ("@Profile", profileId.ToString())));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ProfileSeparators WHERE ProfileId = @Profile",
            ("@Profile", profileId.ToString())));
    }

    [Fact]
    public async Task ConcurrentDeleteMod_SameId_DeletesOnceWithoutOrphans()
    {
        var (db, _) = await CreateDatabaseAsync();
        using var _ = db;
        var repository = new ModRepository(db);
        var library = Path.Combine(_root, "library");
        var service = new ModService(repository, library);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [1, 3, 5, 7]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            """{"name":"Concurrent Delete","version":"1.0"}""");
        var id = await service.ImportModAsync(source);
        var mod = (await repository.GetByIdAsync(id))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.DeleteModAsync(id)));

        Assert.Single(results, deleted => deleted);
        Assert.Equal(3, results.Count(deleted => !deleted));
        Assert.Null(await repository.GetByIdAsync(id));
        Assert.False(Directory.Exists(mod.Path));
    }

    [Fact]
    public async Task DeleteMod_AfterDatabaseCommit_FileCleanupFailureKeepsOnlyOrphanFiles()
    {
        var (db, _) = await CreateDatabaseAsync();
        using var _ = db;
        var repository = new ModRepository(db);
        var library = Path.Combine(_root, "library");
        var service = new ModService(repository, library);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [1, 3, 5, 7]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            """{"name":"Cleanup Timing","version":"1.0"}""");
        var id = await service.ImportModAsync(source);
        var mod = (await repository.GetByIdAsync(id))!;
        var lockedFile = Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories).First();

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await service.DeleteModAsync(id));
            Assert.Null(await repository.GetByIdAsync(id));
            Assert.True(File.Exists(lockedFile));
        }
    }

    [Fact]
    public async Task ProfileUpdate_ReinsertFailure_RollsBackDeletedMembersAndMetadata()
    {
        var (db, path) = await CreateDatabaseAsync();
        using var _ = db;
        var now = DateTime.UtcNow.ToString("O");
        var profileId = Guid.NewGuid();
        var firstMod = Guid.NewGuid();
        var secondMod = Guid.NewGuid();
        using (var seed = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(seed, @"
                INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES (@Profile, 'P', @Now, @Now, 0);
                INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES (@First, 'A', '1', @Path, @Now, @Now);
                INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES (@Second, 'B', '1', @Path, @Now, @Now);
                INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES (@Profile, @First, 1, 0);
                INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES (@Profile, @Second, 1, 1);",
                ("@Profile", profileId.ToString()), ("@First", firstMod.ToString()), ("@Second", secondMod.ToString()),
                ("@Now", now), ("@Path", path));
        }

        var repository = new ProfileRepository(db);
        var profile = (await repository.GetByIdAsync(profileId))!;
        profile.Name = "Renamed";
        profile.Mods.Clear();
        profile.Mods.Add(new ProfileMod { ProfileId = profileId, ModId = Guid.NewGuid(), Enabled = true, Order = 0 });

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateAsync(profile));

        using var verify = await db.OpenConnectionAsync();
        Assert.Equal("P", (string?)await ScalarObjectAsync(verify, "SELECT Name FROM Profiles WHERE Id = @Id",
            ("@Id", profileId.ToString())));
        Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM ProfileMods WHERE ProfileId = @Id",
            ("@Id", profileId.ToString())));
    }

    private async Task<(HD2MimntDbContext Db, string ModPath)> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(_root);
        var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        return (db, Path.Combine(_root, "mod"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<object?> ScalarObjectAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
