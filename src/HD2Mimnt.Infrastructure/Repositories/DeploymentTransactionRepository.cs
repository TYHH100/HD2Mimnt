using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository for deployment transaction tracking
/// </summary>
public class DeploymentTransactionRepository : IDeploymentTransactionRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public DeploymentTransactionRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(DeploymentTransaction transaction, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO DeploymentTransactions (
                Id, SessionId, ProfileId, LogicalModId, ModId, ComponentId,
                Operation, Method, Status, AddedFiles, UpdatedFiles, RemovedFiles,
                SkippedFiles, FileHashes, Conflicts, StartTime, EndTime, ErrorMessage, Metadata
            ) VALUES (
                @Id, @SessionId, @ProfileId, @LogicalModId, @ModId, @ComponentId,
                @Operation, @Method, @Status, @AddedFiles, @UpdatedFiles, @RemovedFiles,
                @SkippedFiles, @FileHashes, @Conflicts, @StartTime, @EndTime, @ErrorMessage, @Metadata
            )";

        command.Parameters.AddWithValue("@Id", transaction.Id.ToString());
        command.Parameters.AddWithValue("@SessionId", transaction.SessionId.ToString());
        command.Parameters.AddWithValue("@ProfileId", transaction.ProfileId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LogicalModId", transaction.LogicalModId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ModId", transaction.ModId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ComponentId", transaction.ComponentId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Operation", transaction.Operation);
        command.Parameters.AddWithValue("@Method", transaction.Method.ToString());
        command.Parameters.AddWithValue("@Status", transaction.Status.ToString());
        command.Parameters.AddWithValue("@AddedFiles", JsonSerializer.Serialize(transaction.AddedFiles));
        command.Parameters.AddWithValue("@UpdatedFiles", JsonSerializer.Serialize(transaction.UpdatedFiles));
        command.Parameters.AddWithValue("@RemovedFiles", JsonSerializer.Serialize(transaction.RemovedFiles));
        command.Parameters.AddWithValue("@SkippedFiles", JsonSerializer.Serialize(transaction.SkippedFiles));
        command.Parameters.AddWithValue("@FileHashes", JsonSerializer.Serialize(transaction.FileHashes));
        command.Parameters.AddWithValue("@Conflicts", JsonSerializer.Serialize(transaction.Conflicts));
        command.Parameters.AddWithValue("@StartTime", transaction.StartTime);
        command.Parameters.AddWithValue("@EndTime", transaction.EndTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ErrorMessage", transaction.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(transaction.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeploymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToTransaction(reader);
        }

        return null;
    }

    public async Task<DeploymentTransaction?> GetBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions WHERE SessionId = @SessionId";
        command.Parameters.AddWithValue("@SessionId", sessionId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToTransaction(reader);
        }

        return null;
    }

    public async Task<List<DeploymentTransaction>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var transactions = new List<DeploymentTransaction>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions WHERE ProfileId = @ProfileId ORDER BY StartTime DESC";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(MapToTransaction(reader));
        }

        return transactions;
    }

    public async Task<List<DeploymentTransaction>> GetByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var transactions = new List<DeploymentTransaction>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions WHERE LogicalModId = @LogicalModId ORDER BY StartTime DESC";
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(MapToTransaction(reader));
        }

        return transactions;
    }

    public async Task<List<DeploymentTransaction>> GetByModIdAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var transactions = new List<DeploymentTransaction>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions WHERE ModId = @ModId ORDER BY StartTime DESC";
        command.Parameters.AddWithValue("@ModId", modId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(MapToTransaction(reader));
        }

        return transactions;
    }

    public async Task<DeploymentTransaction?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeploymentTransactions ORDER BY StartTime DESC LIMIT 1";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToTransaction(reader);
        }

        return null;
    }

    public async Task UpdateStatusAsync(Guid id, DeploymentStatus status, DateTime? endTime = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE DeploymentTransactions
            SET Status = @Status, EndTime = @EndTime, ErrorMessage = @ErrorMessage
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@EndTime", endTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ErrorMessage", errorMessage ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentTransactions WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DeploymentTransactions WHERE SessionId = @SessionId";
        command.Parameters.AddWithValue("@SessionId", sessionId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DeploymentTransaction MapToTransaction(SqliteDataReader reader)
    {
        var profileIdStr = reader.IsDBNull(reader.GetOrdinal("ProfileId")) ? null : reader.GetString(reader.GetOrdinal("ProfileId"));
        var logicalModIdStr = reader.IsDBNull(reader.GetOrdinal("LogicalModId")) ? null : reader.GetString(reader.GetOrdinal("LogicalModId"));
        var modIdStr = reader.IsDBNull(reader.GetOrdinal("ModId")) ? null : reader.GetString(reader.GetOrdinal("ModId"));
        var componentIdStr = reader.IsDBNull(reader.GetOrdinal("ComponentId")) ? null : reader.GetString(reader.GetOrdinal("ComponentId"));

        return new DeploymentTransaction
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            SessionId = Guid.Parse(reader.GetString(reader.GetOrdinal("SessionId"))),
            ProfileId = profileIdStr != null ? Guid.Parse(profileIdStr) : null,
            LogicalModId = logicalModIdStr != null ? Guid.Parse(logicalModIdStr) : null,
            ModId = modIdStr != null ? Guid.Parse(modIdStr) : null,
            ComponentId = componentIdStr != null ? Guid.Parse(componentIdStr) : null,
            Operation = reader.GetString(reader.GetOrdinal("Operation")),
            Method = Enum.Parse<DeploymentMethod>(reader.GetString(reader.GetOrdinal("Method"))),
            Status = Enum.Parse<DeploymentStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            AddedFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("AddedFiles"))) ?? new(),
            UpdatedFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("UpdatedFiles"))) ?? new(),
            RemovedFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("RemovedFiles"))) ?? new(),
            SkippedFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("SkippedFiles"))) ?? new(),
            FileHashes = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("FileHashes"))) ?? new(),
            Conflicts = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Conflicts"))) ?? new(),
            StartTime = reader.GetDateTime(reader.GetOrdinal("StartTime")),
            EndTime = reader.IsDBNull(reader.GetOrdinal("EndTime")) ? null : reader.GetDateTime(reader.GetOrdinal("EndTime")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("Metadata"))) ?? new()
        };
    }
}
