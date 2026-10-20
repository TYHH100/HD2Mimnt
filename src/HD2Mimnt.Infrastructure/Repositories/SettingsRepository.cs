using HD2Mimnt.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>短连接不参与 DbContext 的共享连接生命周期，恢复数据库时无须额外释放。</summary>
public sealed class SettingsRepository(string databasePath) : ISettingsRepository
{
    private const string LegacyMigration = "settings-json-v1";

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS SettingsMigrations (Id TEXT PRIMARY KEY);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public Dictionary<string, string> ReadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>();
        while (reader.Read()) values.Add(reader.GetString(0), reader.GetString(1));
        return values;
    }

    public bool IsLegacyMigrationComplete()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SettingsMigrations WHERE Id = $id";
        command.Parameters.AddWithValue("$id", LegacyMigration);
        return (long)command.ExecuteScalar()! != 0;
    }

    public void ImportLegacy(IReadOnlyDictionary<string, string> values)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = "INSERT OR IGNORE INTO SettingsMigrations (Id) VALUES ($id)";
        marker.Parameters.AddWithValue("$id", LegacyMigration);
        if (marker.ExecuteNonQuery() != 0)
            foreach (var (key, value) in values) Write(connection, transaction, key, value, overwrite: false);
        transaction.Commit();
    }

    public Task ApplyAsync(IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> removedKeys)
        // Microsoft.Data.Sqlite 的异步 API 实际同步执行；把写事务移出 UI 线程。
        => Task.Run(() =>
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var key in removedKeys)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM Settings WHERE Key = $key";
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }
            foreach (var (key, value) in values) Write(connection, transaction, key, value, overwrite: true);
            transaction.Commit();
        });

    public Task ResetAsync(string? category) => Task.Run(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = category == null ? "DELETE FROM Settings" :
            "DELETE FROM Settings WHERE substr(Key, 1, length($prefix)) = $prefix COLLATE NOCASE";
        if (category != null) command.Parameters.AddWithValue("$prefix", category + ".");
        command.ExecuteNonQuery();
    });

    public Task ResetCategoriesAsync(IReadOnlyCollection<string> categories) => Task.Run(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var category in categories)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Settings WHERE substr(Key, 1, length($prefix)) = $prefix COLLATE NOCASE";
            command.Parameters.AddWithValue("$prefix", category + ".");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    });

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, string key, string value, bool overwrite)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) " +
            (overwrite ? "DO UPDATE SET Value = excluded.Value" : "DO NOTHING");
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
