using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 模组删除必须“先删库、后删文件”：数据库删除失败时源文件必须完整保留，供用户重试。
/// </summary>
[Trait("Category", "Slow")]
public sealed class ModDeletionSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntDeleteSafety",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeleteMod_DatabaseFailure_KeepsSourceFilesAndRow()
    {
        Directory.CreateDirectory(_root);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        var repository = new ModRepository(db);
        var library = Path.Combine(_root, "library");
        var mods = new ModService(repository, library);

        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [1, 3, 5, 7]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            """{"name":"Deletion Safety","version":"1.0"}""");
        var id = await mods.ImportModAsync(source);
        var mod = (await repository.GetByIdAsync(id))!;
        var sourceFile = Path.Combine(mod.Path, "0123456789abcdef.patch_0");
        Assert.True(File.Exists(sourceFile));

        using (var connection = await db.OpenConnectionAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER block_mod_delete BEFORE DELETE ON Mods BEGIN SELECT RAISE(ABORT, 'blocked'); END";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => mods.DeleteModAsync(id));

        Assert.True(File.Exists(sourceFile));
        Assert.NotNull(await repository.GetByIdAsync(id));
    }

    [Fact]
    public async Task DeleteMod_PathAtLibraryRoot_DoesNotDeleteLibrary()
    {
        Directory.CreateDirectory(_root);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        var repository = new ModRepository(db);
        var library = Path.Combine(_root, "library");
        var mods = new ModService(repository, library);

        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "0123456789abcdef.patch_0"), [1, 3, 5, 7]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            """{"name":"Library Root Safety","version":"1.0"}""");
        var id = await mods.ImportModAsync(source);

        var sentinel = Path.Combine(library, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        using (var connection = await db.OpenConnectionAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Mods SET Path = @Path WHERE Id = @Id";
            command.Parameters.AddWithValue("@Path", library);
            command.Parameters.AddWithValue("@Id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(await mods.DeleteModAsync(id));

        Assert.True(File.Exists(sentinel));
        Assert.True(Directory.Exists(library));
        Assert.Null(await repository.GetByIdAsync(id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
