using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Infrastructure;

/// <summary>
/// 数据库完整性自检的回归测试：健康库不能被改写，损坏库必须修复到可校验一致。
/// 索引损坏（SQLite Error 11）曾让整个界面刷新/部署链路失效，这里用真实页损坏复现该场景。
/// </summary>
[Trait("Category", "Slow")]
public class DatabaseIntegrityGuardTests : IDisposable
{
    // 每个用例独立根目录，避免清理时误删共享的 %TEMP%\Backups。
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"guard_tests_{Guid.NewGuid():N}");

    private string CreateDatabase(int rows = 400)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"guard_{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE Mods (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Version TEXT NOT NULL, Path TEXT NOT NULL);
                CREATE INDEX idx_mods_name ON Mods(Name);
                CREATE TABLE Profiles (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, IsActive INTEGER NOT NULL);
                CREATE TABLE Tasks (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Type TEXT NOT NULL, Status INTEGER NOT NULL,
                    Progress REAL, Stage TEXT, CurrentItem TEXT, Message TEXT, StartTime TEXT NOT NULL,
                    EndTime TEXT, Errors TEXT, Result TEXT);
                CREATE INDEX idx_tasks_status ON Tasks(Status);
                CREATE TABLE DeploymentFiles (
                    Id TEXT PRIMARY KEY, DeploymentId TEXT NOT NULL, ModId TEXT NOT NULL,
                    SourcePath TEXT NOT NULL, TargetPath TEXT NOT NULL, Method TEXT, GamePath TEXT);
                CREATE INDEX idx_deploymentfiles_target ON DeploymentFiles(TargetPath);
                CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                """;
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO Mods (Id, Name, Version, Path) VALUES (@Id, @Name, '1.0.0', @Path)";
            var id = insert.Parameters.Add("@Id", SqliteType.Text);
            var name = insert.Parameters.Add("@Name", SqliteType.Text);
            var pathParameter = insert.Parameters.Add("@Path", SqliteType.Text);
            for (var i = 0; i < rows; i++)
            {
                id.Value = Guid.NewGuid().ToString();
                name.Value = $"模组 {i:D4}";
                pathParameter.Value = $@"C:\mods\{i:D4}";
                insert.ExecuteNonQuery();
            }
        }
        using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = "INSERT INTO Profiles (Id, Name, IsActive) VALUES (@Id, '日常游玩', 1)";
            profile.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            profile.ExecuteNonQuery();
        }
        transaction.Commit();
        return path;
    }

    private static long RootPage(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = @Name";
        command.Parameters.AddWithValue("@Name", table);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void CorruptPage(string path, long pageNumber, int pageSize = 4096)
    {
        // 连接池会持有文件句柄，直接改字节前必须清空，否则文件处于占用状态。
        SqliteConnection.ClearAllPools();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = (pageNumber - 1) * pageSize;
        var garbage = new byte[pageSize];
        Random.Shared.NextBytes(garbage);
        stream.Write(garbage, 0, garbage.Length);
    }

    [Fact]
    public async Task EnsureHealthy_KeepsHealthyDatabaseUntouched()
    {
        var path = CreateDatabase();
        var before = new FileInfo(path).Length;

        var outcome = await DatabaseIntegrityGuard.EnsureHealthyAsync(path);

        Assert.False(outcome.Repaired);
        Assert.Null(outcome.BackupDirectory);
        Assert.Equal(before, new FileInfo(path).Length);
    }

    [Fact]
    public async Task EnsureHealthy_RepairsCorruptedTablePage()
    {
        var path = CreateDatabase();
        CorruptPage(path, RootPage(path, "Mods"));

        var outcome = await DatabaseIntegrityGuard.EnsureHealthyAsync(path);

        Assert.True(outcome.Repaired);
        Assert.NotNull(outcome.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(outcome.BackupDirectory!, Path.GetFileName(path))));
        // 修复后的库必须能通过校验，且未受损的表数据保持完整。
        var second = await DatabaseIntegrityGuard.EnsureHealthyAsync(path);
        Assert.False(second.Repaired);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Profiles";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task EnsureHealthy_DetectOnly_DoesNotTouchCorruptDatabase()
    {
        var path = CreateDatabase();
        CorruptPage(path, RootPage(path, "Mods"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseIntegrityGuard.EnsureHealthyAsync(path, allowRepair: false));

        Assert.False(Directory.Exists(Path.Combine(_root, "Backups")));
        Assert.False(await DatabaseIntegrityGuard.IsHealthyAsync(path, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_root, ".repair-*"));
    }

    [Fact]
    public async Task EnsureHealthy_UnrecoverableDatabase_ThrowsWithBackupPath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"garbage_{Guid.NewGuid():N}.db");
        var garbage = new byte[8192];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(path, garbage);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseIntegrityGuard.EnsureHealthyAsync(path));

        Assert.Contains("Backups", error.Message, StringComparison.Ordinal);
        var backupDirectory = Path.Combine(_root, "Backups");
        Assert.True(Directory.Exists(backupDirectory));
        Assert.NotEmpty(Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
