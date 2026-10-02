using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Data;

/// <summary>
/// SQLite 数据库上下文
/// 使用 WAL 模式以提高并发性能
/// </summary>
public class HD2MimntDbContext : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public HD2MimntDbContext(string databasePath)
    {
        _connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;";
    }

    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);

            // 启用 WAL 模式
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return _connection;
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);

        var sql = @"
            CREATE TABLE IF NOT EXISTS Mods (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Version TEXT NOT NULL,
                Author TEXT,
                Description TEXT,
                Path TEXT NOT NULL,
                Category INTEGER NOT NULL DEFAULT 0,
                Tags TEXT,
                WorkflowType TEXT,
                ImportedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                Size INTEGER,
                Hash TEXT,
                ThumbnailPath TEXT,
                Source INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                ManifestJson TEXT,
                ContainsScripts INTEGER NOT NULL DEFAULT 0,
                NexusModId INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_mods_name ON Mods(Name);
            CREATE INDEX IF NOT EXISTS idx_mods_category ON Mods(Category);

            CREATE TABLE IF NOT EXISTS Profiles (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE,
                Description TEXT,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                IsActive INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ProfileMods (
                ProfileId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                [Order] INTEGER NOT NULL,
                PRIMARY KEY (ProfileId, ModId),
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE,
                FOREIGN KEY (ModId) REFERENCES Mods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_profilemods_order ON ProfileMods([Order]);

            CREATE TABLE IF NOT EXISTS Tasks (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Progress REAL,
                Stage TEXT,
                CurrentItem TEXT,
                Message TEXT,
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                Errors TEXT,
                Result TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_status ON Tasks(Status);
            CREATE INDEX IF NOT EXISTS idx_tasks_starttime ON Tasks(StartTime DESC);

            CREATE TABLE IF NOT EXISTS DeploymentRecords (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                Workflow TEXT,
                DeploymentMethod TEXT,
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                Status TEXT,
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id),
                FOREIGN KEY (ModId) REFERENCES Mods(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_deployment_session ON DeploymentRecords(SessionId);
            CREATE INDEX IF NOT EXISTS idx_deployment_mod ON DeploymentRecords(ModId);

            CREATE TABLE IF NOT EXISTS DeploymentFiles (
                Id TEXT PRIMARY KEY,
                DeploymentId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                Method TEXT,
                FOREIGN KEY (DeploymentId) REFERENCES DeploymentRecords(Id) ON DELETE CASCADE,
                FOREIGN KEY (ModId) REFERENCES Mods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_deployment ON DeploymentFiles(DeploymentId);
            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_mod ON DeploymentFiles(ModId);
            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_target ON DeploymentFiles(TargetPath);

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_connection != null)
        {
            try
            {
                // 在关闭前禁用 WAL 模式，清理额外的文件
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                command.ExecuteNonQuery();
            }
            catch
            {
                // 忽略错误，继续关闭连接
            }

            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }

        // 强制 GC 以确保文件锁释放
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
