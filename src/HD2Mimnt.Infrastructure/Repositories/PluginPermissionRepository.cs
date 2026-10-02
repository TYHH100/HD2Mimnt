using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// SQLite implementation of IPluginPermissionRepository
/// </summary>
public class PluginPermissionRepository : IPluginPermissionRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public PluginPermissionRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<PluginPermission>> GetByPluginIdAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, PluginId, Permission, Granted, GrantedAt
            FROM PluginPermissions
            WHERE PluginId = @PluginId
            ORDER BY Permission";
        command.Parameters.AddWithValue("@PluginId", pluginId);

        var permissions = new List<PluginPermission>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(MapPermission(reader));
        }

        return permissions;
    }

    public async Task AddAsync(PluginPermission permission, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PluginPermissions (Id, PluginId, Permission, Granted, GrantedAt)
            VALUES (@Id, @PluginId, @Permission, @Granted, @GrantedAt)";

        command.Parameters.AddWithValue("@Id", permission.Id);
        command.Parameters.AddWithValue("@PluginId", permission.PluginId);
        command.Parameters.AddWithValue("@Permission", permission.Permission);
        command.Parameters.AddWithValue("@Granted", permission.Granted ? 1 : 0);
        command.Parameters.AddWithValue("@GrantedAt", permission.GrantedAt?.ToString("O") ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(PluginPermission permission, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE PluginPermissions
            SET Permission = @Permission, Granted = @Granted, GrantedAt = @GrantedAt
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", permission.Id);
        command.Parameters.AddWithValue("@Permission", permission.Permission);
        command.Parameters.AddWithValue("@Granted", permission.Granted ? 1 : 0);
        command.Parameters.AddWithValue("@GrantedAt", permission.GrantedAt?.ToString("O") ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByPluginIdAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PluginPermissions WHERE PluginId = @PluginId";
        command.Parameters.AddWithValue("@PluginId", pluginId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PluginPermission MapPermission(SqliteDataReader reader)
    {
        return new PluginPermission
        {
            Id = reader.GetString(0),
            PluginId = reader.GetString(1),
            Permission = reader.GetString(2),
            Granted = reader.GetInt32(3) == 1,
            GrantedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4))
        };
    }
}
