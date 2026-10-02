using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// SQLite implementation of IPluginRepository
/// </summary>
public class PluginRepository : IPluginRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public PluginRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Plugin?> GetByIdAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Version, Author, Description, ManifestPath, AssemblyPath,
                   InstalledAt, Enabled, PermissionMode
            FROM Plugins
            WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", pluginId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapPlugin(reader);
        }

        return null;
    }

    public async Task<List<Plugin>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Version, Author, Description, ManifestPath, AssemblyPath,
                   InstalledAt, Enabled, PermissionMode
            FROM Plugins
            ORDER BY Name";

        var plugins = new List<Plugin>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plugins.Add(MapPlugin(reader));
        }

        return plugins;
    }

    public async Task<List<Plugin>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Version, Author, Description, ManifestPath, AssemblyPath,
                   InstalledAt, Enabled, PermissionMode
            FROM Plugins
            WHERE Enabled = 1
            ORDER BY Name";

        var plugins = new List<Plugin>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plugins.Add(MapPlugin(reader));
        }

        return plugins;
    }

    public async Task AddAsync(Plugin plugin, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Plugins (Id, Name, Version, Author, Description, ManifestPath,
                                AssemblyPath, InstalledAt, Enabled, PermissionMode)
            VALUES (@Id, @Name, @Version, @Author, @Description, @ManifestPath,
                    @AssemblyPath, @InstalledAt, @Enabled, @PermissionMode)";

        command.Parameters.AddWithValue("@Id", plugin.Id);
        command.Parameters.AddWithValue("@Name", plugin.Name);
        command.Parameters.AddWithValue("@Version", plugin.Version);
        command.Parameters.AddWithValue("@Author", plugin.Author ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", plugin.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ManifestPath", plugin.ManifestPath);
        command.Parameters.AddWithValue("@AssemblyPath", plugin.AssemblyPath);
        command.Parameters.AddWithValue("@InstalledAt", plugin.InstalledAt.ToString("O"));
        command.Parameters.AddWithValue("@Enabled", plugin.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@PermissionMode", plugin.PermissionMode);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(Plugin plugin, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Plugins
            SET Name = @Name, Version = @Version, Author = @Author, Description = @Description,
                ManifestPath = @ManifestPath, AssemblyPath = @AssemblyPath,
                Enabled = @Enabled, PermissionMode = @PermissionMode
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", plugin.Id);
        command.Parameters.AddWithValue("@Name", plugin.Name);
        command.Parameters.AddWithValue("@Version", plugin.Version);
        command.Parameters.AddWithValue("@Author", plugin.Author ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", plugin.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ManifestPath", plugin.ManifestPath);
        command.Parameters.AddWithValue("@AssemblyPath", plugin.AssemblyPath);
        command.Parameters.AddWithValue("@Enabled", plugin.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@PermissionMode", plugin.PermissionMode);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Plugins WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", pluginId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Plugins WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", pluginId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) > 0;
    }

    private static Plugin MapPlugin(SqliteDataReader reader)
    {
        return new Plugin
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Version = reader.GetString(2),
            Author = reader.IsDBNull(3) ? null : reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            ManifestPath = reader.GetString(5),
            AssemblyPath = reader.GetString(6),
            InstalledAt = DateTime.Parse(reader.GetString(7)),
            Enabled = reader.GetInt32(8) == 1,
            PermissionMode = reader.GetString(9)
        };
    }
}
