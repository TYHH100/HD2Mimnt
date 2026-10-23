using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// 部署记录仓储实现
/// 使用 Dapper/raw SQL
/// </summary>
public class DeploymentRepository : IDeploymentRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public DeploymentRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DeploymentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentRecords WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var record = MapToDeploymentRecord(reader);
            await reader.CloseAsync();
            record.Files = (await GetDeploymentFilesAsync(record.Id, cancellationToken)).ToList();
            return record;
        }

        return null;
    }

    public async Task<IEnumerable<DeploymentRecord>> GetBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentRecords WHERE SessionId = @SessionId ORDER BY StartTime DESC";
        command.Parameters.AddWithValue("@SessionId", sessionId.ToString());

        var records = new List<DeploymentRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapToDeploymentRecord(reader));
        }

        // Load files for each record
        foreach (var record in records)
        {
            record.Files = (await GetDeploymentFilesAsync(record.Id, cancellationToken)).ToList();
        }

        return records;
    }

    public async Task<IEnumerable<DeploymentRecord>> GetByModIdAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentRecords WHERE ModId = @ModId ORDER BY StartTime DESC";
        command.Parameters.AddWithValue("@ModId", modId.ToString());

        var records = new List<DeploymentRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapToDeploymentRecord(reader));
        }

        foreach (var record in records)
        {
            record.Files = (await GetDeploymentFilesAsync(record.Id, cancellationToken)).ToList();
        }

        return records;
    }

    public async Task<Guid?> GetLatestSessionIdAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId FROM DeploymentRecords ORDER BY StartTime DESC LIMIT 1";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null ? Guid.Parse(result.ToString()!) : null;
    }

    public async Task<IEnumerable<DeploymentRecord>> GetLatestDeploymentsAsync(CancellationToken cancellationToken = default)
    {
        var latestSessionId = await GetLatestSessionIdAsync(cancellationToken);
        if (latestSessionId == null)
        {
            return Enumerable.Empty<DeploymentRecord>();
        }

        return await GetBySessionIdAsync(latestSessionId.Value, cancellationToken);
    }

    public async Task AddAsync(DeploymentRecord record, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO DeploymentRecords (Id, SessionId, ProfileId, ModId, Workflow, DeploymentMethod, StartTime, EndTime, Status, OptionsSnapshotJson, DeploymentOrderIndex, ModVersion)
                VALUES (@Id, @SessionId, @ProfileId, @ModId, @Workflow, @DeploymentMethod, @StartTime, @EndTime, @Status, @OptionsSnapshotJson, @DeploymentOrderIndex, @ModVersion)";

            AddDeploymentRecordParameters(command, record);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // Add files
            if (record.Files.Any())
            {
                await AddDeploymentFilesInternalAsync(record.Files, connection, transaction, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateAsync(DeploymentRecord record, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE DeploymentRecords
            SET SessionId = @SessionId, ProfileId = @ProfileId, ModId = @ModId,
                Workflow = @Workflow, DeploymentMethod = @DeploymentMethod,
                StartTime = @StartTime, EndTime = @EndTime, Status = @Status,
                OptionsSnapshotJson = @OptionsSnapshotJson, DeploymentOrderIndex = @DeploymentOrderIndex, ModVersion = @ModVersion
            WHERE Id = @Id";

        AddDeploymentRecordParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentRecords WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentRecords WHERE SessionId = @SessionId";
        command.Parameters.AddWithValue("@SessionId", sessionId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByModIdAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentRecords WHERE ModId = @ModId";
        command.Parameters.AddWithValue("@ModId", modId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddDeploymentFileAsync(DeploymentFile file, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath, Method)
            VALUES (@Id, @DeploymentId, @ModId, @SourcePath, @TargetPath, @Method)";

        AddDeploymentFileParameters(command, file);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddDeploymentFilesAsync(IEnumerable<DeploymentFile> files, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            await AddDeploymentFilesInternalAsync(files, connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IEnumerable<DeploymentFile>> GetDeploymentFilesAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentFiles WHERE DeploymentId = @DeploymentId";
        command.Parameters.AddWithValue("@DeploymentId", deploymentId.ToString());

        var files = new List<DeploymentFile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(MapToDeploymentFile(reader));
        }

        return files;
    }

    public async Task<DeploymentFile?> GetDeploymentFileByTargetPathAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentFiles WHERE TargetPath = @TargetPath LIMIT 1";
        command.Parameters.AddWithValue("@TargetPath", targetPath);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToDeploymentFile(reader);
        }

        return null;
    }

    private async Task AddDeploymentFilesInternalAsync(
        IEnumerable<DeploymentFile> files,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath, Method)
                VALUES (@Id, @DeploymentId, @ModId, @SourcePath, @TargetPath, @Method)";

            AddDeploymentFileParameters(command, file);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static DeploymentRecord MapToDeploymentRecord(SqliteDataReader reader)
    {
        return new DeploymentRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            SessionId = Guid.Parse(reader.GetString(reader.GetOrdinal("SessionId"))),
            ProfileId = Guid.Parse(reader.GetString(reader.GetOrdinal("ProfileId"))),
            ModId = Guid.Parse(reader.GetString(reader.GetOrdinal("ModId"))),
            Workflow = reader.IsDBNull(reader.GetOrdinal("Workflow")) ? string.Empty : reader.GetString(reader.GetOrdinal("Workflow")),
            DeploymentMethod = reader.IsDBNull(reader.GetOrdinal("DeploymentMethod")) ? string.Empty : reader.GetString(reader.GetOrdinal("DeploymentMethod")),
            StartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartTime"))),
            EndTime = reader.IsDBNull(reader.GetOrdinal("EndTime")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("EndTime"))),
            Status = reader.IsDBNull(reader.GetOrdinal("Status")) ? string.Empty : reader.GetString(reader.GetOrdinal("Status")),
            OptionsSnapshotJson = reader.IsDBNull(reader.GetOrdinal("OptionsSnapshotJson")) ? null : reader.GetString(reader.GetOrdinal("OptionsSnapshotJson")),
            DeploymentOrderIndex = reader.IsDBNull(reader.GetOrdinal("DeploymentOrderIndex")) ? null : reader.GetInt32(reader.GetOrdinal("DeploymentOrderIndex")),
            ModVersion = reader.IsDBNull(reader.GetOrdinal("ModVersion")) ? null : reader.GetString(reader.GetOrdinal("ModVersion"))
        };
    }

    internal static DeploymentFile MapToDeploymentFile(SqliteDataReader reader)
    {
        return new DeploymentFile
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            DeploymentId = Guid.Parse(reader.GetString(reader.GetOrdinal("DeploymentId"))),
            ModId = Guid.Parse(reader.GetString(reader.GetOrdinal("ModId"))),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            TargetPath = reader.GetString(reader.GetOrdinal("TargetPath")),
            Method = reader.IsDBNull(reader.GetOrdinal("Method")) ? string.Empty : reader.GetString(reader.GetOrdinal("Method"))
        };
    }

    private static void AddDeploymentRecordParameters(SqliteCommand command, DeploymentRecord record)
    {
        command.Parameters.AddWithValue("@Id", record.Id.ToString());
        command.Parameters.AddWithValue("@SessionId", record.SessionId.ToString());
        command.Parameters.AddWithValue("@ProfileId", record.ProfileId.ToString());
        command.Parameters.AddWithValue("@ModId", record.ModId.ToString());
        command.Parameters.AddWithValue("@Workflow", record.Workflow ?? string.Empty);
        command.Parameters.AddWithValue("@DeploymentMethod", record.DeploymentMethod ?? string.Empty);
        command.Parameters.AddWithValue("@StartTime", record.StartTime.ToString("O"));
        command.Parameters.AddWithValue("@EndTime", record.EndTime?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", record.Status ?? string.Empty);
        command.Parameters.AddWithValue("@OptionsSnapshotJson", (object?)record.OptionsSnapshotJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@DeploymentOrderIndex", (object?)record.DeploymentOrderIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("@ModVersion", (object?)record.ModVersion ?? DBNull.Value);
    }

    private static void AddDeploymentFileParameters(SqliteCommand command, DeploymentFile file)
    {
        command.Parameters.AddWithValue("@Id", file.Id.ToString());
        command.Parameters.AddWithValue("@DeploymentId", file.DeploymentId.ToString());
        command.Parameters.AddWithValue("@ModId", file.ModId.ToString());
        command.Parameters.AddWithValue("@SourcePath", file.SourcePath);
        command.Parameters.AddWithValue("@TargetPath", file.TargetPath);
        command.Parameters.AddWithValue("@Method", file.Method ?? string.Empty);
    }

    public async Task DeleteDeploymentFilesByDeploymentIdAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentFiles WHERE DeploymentId = @DeploymentId";
        command.Parameters.AddWithValue("@DeploymentId", deploymentId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CommitModDeploymentRemovalAsync(IReadOnlyCollection<Guid> deploymentIds,
        IReadOnlyList<(Guid FileId, string TargetPath)> renamedFiles, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var file in renamedFiles)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE DeploymentFiles SET TargetPath = @TargetPath WHERE Id = @Id";
            command.Parameters.AddWithValue("@TargetPath", file.TargetPath);
            command.Parameters.AddWithValue("@Id", file.FileId.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"部署文件记录已变更: {file.FileId}");
        }
        foreach (var id in deploymentIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM DeploymentRecords WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
