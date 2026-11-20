using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Infrastructure;

public sealed class SqlForeignKeysTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sql_probe_{Guid.NewGuid():N}");

    [Fact]
    public async Task ForeignKeys_AreEnabled_AndDeleteProfileCascadesMembers()
    {
        Directory.CreateDirectory(_root);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await db.InitializeDatabaseAsync();
        var profileId = Guid.NewGuid();
        var modId = Guid.NewGuid();
        using (var connection = await db.OpenConnectionAsync())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys";
                Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES (@Profile, 'P', @Now, @Now, 0);
                    INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES (@Mod, 'M', '1', 'C:\m', @Now, @Now);
                    INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES (@Profile, @Mod, 1, 0);";
                command.Parameters.AddWithValue("@Profile", profileId.ToString());
                command.Parameters.AddWithValue("@Mod", modId.ToString());
                command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM Profiles WHERE Id = @Id";
                command.Parameters.AddWithValue("@Id", profileId.ToString());
                await command.ExecuteNonQueryAsync();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM ProfileMods WHERE ProfileId = @Id";
                command.Parameters.AddWithValue("@Id", profileId.ToString());
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
