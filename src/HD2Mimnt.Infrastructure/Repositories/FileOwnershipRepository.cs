using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository for file ownership tracking
/// </summary>
public class FileOwnershipRepository : IFileOwnershipRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public FileOwnershipRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(FileOwnership ownership, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO FileOwnerships (Id, LogicalModId, ComponentId, PackageId, DeployPath, ContentHash, TransactionId, DeployedAt, IsShared, ReferenceCount)
            VALUES (@Id, @LogicalModId, @ComponentId, @PackageId, @DeployPath, @ContentHash, @TransactionId, @DeployedAt, @IsShared, @ReferenceCount)";

        command.Parameters.AddWithValue("@Id", ownership.Id.ToString());
        command.Parameters.AddWithValue("@LogicalModId", ownership.LogicalModId.ToString());
        command.Parameters.AddWithValue("@ComponentId", ownership.ComponentId.ToString());
        command.Parameters.AddWithValue("@PackageId", ownership.PackageId.ToString());
        command.Parameters.AddWithValue("@DeployPath", ownership.DeployPath);
        command.Parameters.AddWithValue("@ContentHash", ownership.ContentHash);
        command.Parameters.AddWithValue("@TransactionId", ownership.TransactionId.ToString());
        command.Parameters.AddWithValue("@DeployedAt", ownership.DeployedAt);
        command.Parameters.AddWithValue("@IsShared", ownership.IsShared);
        command.Parameters.AddWithValue("@ReferenceCount", ownership.ReferenceCount);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FileOwnership?> GetByDeployPathAsync(string deployPath, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FileOwnerships WHERE DeployPath = @DeployPath";
        command.Parameters.AddWithValue("@DeployPath", deployPath);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToFileOwnership(reader);
        }

        return null;
    }

    public async Task<List<FileOwnership>> GetByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var ownerships = new List<FileOwnership>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FileOwnerships WHERE LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ownerships.Add(MapToFileOwnership(reader));
        }

        return ownerships;
    }

    public async Task<List<FileOwnership>> GetByComponentIdAsync(Guid componentId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var ownerships = new List<FileOwnership>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FileOwnerships WHERE ComponentId = @ComponentId";
        command.Parameters.AddWithValue("@ComponentId", componentId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ownerships.Add(MapToFileOwnership(reader));
        }

        return ownerships;
    }

    public async Task DeleteByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FileOwnerships WHERE LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateReferenceCountAsync(string deployPath, int delta, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE FileOwnerships
            SET ReferenceCount = ReferenceCount + @Delta
            WHERE DeployPath = @DeployPath";

        command.Parameters.AddWithValue("@DeployPath", deployPath);
        command.Parameters.AddWithValue("@Delta", delta);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<FileOwnership>> GetSharedResourcesAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var ownerships = new List<FileOwnership>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FileOwnerships WHERE IsShared = 1";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ownerships.Add(MapToFileOwnership(reader));
        }

        return ownerships;
    }

    public async Task<List<FileOwnership>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var ownerships = new List<FileOwnership>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM FileOwnerships";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ownerships.Add(MapToFileOwnership(reader));
        }

        return ownerships;
    }

    public async Task DeleteByDeployPathAsync(string deployPath, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FileOwnerships WHERE DeployPath = @DeployPath";
        command.Parameters.AddWithValue("@DeployPath", deployPath);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private FileOwnership MapToFileOwnership(SqliteDataReader reader)
    {
        return new FileOwnership
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            LogicalModId = Guid.Parse(reader.GetString(reader.GetOrdinal("LogicalModId"))),
            ComponentId = Guid.Parse(reader.GetString(reader.GetOrdinal("ComponentId"))),
            PackageId = Guid.Parse(reader.GetString(reader.GetOrdinal("PackageId"))),
            DeployPath = reader.GetString(reader.GetOrdinal("DeployPath")),
            ContentHash = reader.GetString(reader.GetOrdinal("ContentHash")),
            TransactionId = Guid.Parse(reader.GetString(reader.GetOrdinal("TransactionId"))),
            DeployedAt = reader.GetDateTime(reader.GetOrdinal("DeployedAt")),
            IsShared = reader.GetBoolean(reader.GetOrdinal("IsShared")),
            ReferenceCount = reader.GetInt32(reader.GetOrdinal("ReferenceCount"))
        };
    }
}
