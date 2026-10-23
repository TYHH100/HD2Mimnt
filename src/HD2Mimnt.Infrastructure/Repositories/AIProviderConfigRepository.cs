using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository implementation for AI provider configuration management
/// </summary>
public class AIProviderConfigRepository : IAIProviderConfigRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public AIProviderConfigRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AIProviderConfigs WHERE IsEnabled = 1 LIMIT 1";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToAIProviderConfig(reader);
        }

        return null;
    }

    public async Task<List<AIProviderConfig>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AIProviderConfigs ORDER BY Name";

        var configs = new List<AIProviderConfig>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            configs.Add(MapToAIProviderConfig(reader));
        }

        return configs;
    }

    public async Task<AIProviderConfig?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AIProviderConfigs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToAIProviderConfig(reader);
        }

        return null;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AIProviderConfigs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    public async Task AddAsync(AIProviderConfig config, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO AIProviderConfigs
            (Id, Name, Type, ApiEndpoint, ApiKey, Model, IsEnabled, MaxTokens, Temperature, CreatedAt, ModifiedAt)
            VALUES
            (@Id, @Name, @Type, @ApiEndpoint, @ApiKey, @Model, @IsEnabled, @MaxTokens, @Temperature, @CreatedAt, @ModifiedAt)";

        AddParameters(command, config);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(AIProviderConfig config, CancellationToken cancellationToken = default)
    {
        config.ModifiedAt = DateTime.UtcNow;

        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE AIProviderConfigs SET
                Name = @Name,
                Type = @Type,
                ApiEndpoint = @ApiEndpoint,
                ApiKey = @ApiKey,
                Model = @Model,
                IsEnabled = @IsEnabled,
                MaxTokens = @MaxTokens,
                Temperature = @Temperature,
                ModifiedAt = @ModifiedAt
            WHERE Id = @Id";

        AddParameters(command, config);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AIProviderConfigs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetActiveProviderAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        // Disable all providers first
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AIProviderConfigs SET IsEnabled = 0";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Enable the specified provider
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE AIProviderConfigs SET IsEnabled = 1 WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static AIProviderConfig MapToAIProviderConfig(SqliteDataReader reader)
    {
        return new AIProviderConfig
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Type = (AIProviderType)reader.GetInt32(reader.GetOrdinal("Type")),
            ApiEndpoint = reader.GetString(reader.GetOrdinal("ApiEndpoint")),
            ApiKey = reader.IsDBNull(reader.GetOrdinal("ApiKey")) ? null : reader.GetString(reader.GetOrdinal("ApiKey")),
            Model = reader.IsDBNull(reader.GetOrdinal("Model")) ? null : reader.GetString(reader.GetOrdinal("Model")),
            IsEnabled = reader.GetInt32(reader.GetOrdinal("IsEnabled")) == 1,
            MaxTokens = reader.GetInt32(reader.GetOrdinal("MaxTokens")),
            Temperature = reader.GetDouble(reader.GetOrdinal("Temperature")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ModifiedAt = reader.IsDBNull(reader.GetOrdinal("ModifiedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ModifiedAt")))
        };
    }

    private static void AddParameters(SqliteCommand command, AIProviderConfig config)
    {
        command.Parameters.AddWithValue("@Id", config.Id);
        command.Parameters.AddWithValue("@Name", config.Name);
        command.Parameters.AddWithValue("@Type", (int)config.Type);
        command.Parameters.AddWithValue("@ApiEndpoint", config.ApiEndpoint);
        command.Parameters.AddWithValue("@ApiKey", (object?)config.ApiKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@Model", (object?)config.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEnabled", config.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@MaxTokens", config.MaxTokens);
        command.Parameters.AddWithValue("@Temperature", config.Temperature);
        command.Parameters.AddWithValue("@CreatedAt", config.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ModifiedAt", config.ModifiedAt?.ToString("O") ?? (object)DBNull.Value);
    }
}
