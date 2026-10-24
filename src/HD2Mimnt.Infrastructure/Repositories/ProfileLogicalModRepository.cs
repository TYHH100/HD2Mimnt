using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository for managing Profile-LogicalMod associations
/// </summary>
public class ProfileLogicalModRepository : IProfileLogicalModRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public ProfileLogicalModRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ProfileLogicalMod profileLogicalMod, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ProfileLogicalMods (ProfileId, LogicalModId, Enabled, [Order], EnabledComponentIds, ComponentConfigurations)
            VALUES (@ProfileId, @LogicalModId, @Enabled, @Order, @EnabledComponentIds, @ComponentConfigurations)";

        command.Parameters.AddWithValue("@ProfileId", profileLogicalMod.ProfileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", profileLogicalMod.LogicalModId.ToString());
        command.Parameters.AddWithValue("@Enabled", profileLogicalMod.Enabled);
        command.Parameters.AddWithValue("@Order", profileLogicalMod.Order);
        command.Parameters.AddWithValue("@EnabledComponentIds", JsonSerializer.Serialize(profileLogicalMod.EnabledComponentIds));
        command.Parameters.AddWithValue("@ComponentConfigurations", JsonSerializer.Serialize(profileLogicalMod.ComponentConfigurations));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<ProfileLogicalMod>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var profileLogicalMods = new List<ProfileLogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ProfileLogicalMods WHERE ProfileId = @ProfileId ORDER BY [Order]";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profileLogicalMods.Add(MapToProfileLogicalMod(reader));
        }

        return profileLogicalMods;
    }

    public async Task<List<ProfileLogicalMod>> GetEnabledByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var profileLogicalMods = new List<ProfileLogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ProfileLogicalMods WHERE ProfileId = @ProfileId AND Enabled = 1 ORDER BY [Order]";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profileLogicalMods.Add(MapToProfileLogicalMod(reader));
        }

        return profileLogicalMods;
    }

    public async Task UpdateEnabledAsync(Guid profileId, Guid logicalModId, bool enabled, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE ProfileLogicalMods
            SET Enabled = @Enabled
            WHERE ProfileId = @ProfileId AND LogicalModId = @LogicalModId";

        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());
        command.Parameters.AddWithValue("@Enabled", enabled);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateOrderAsync(Guid profileId, Guid logicalModId, int order, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE ProfileLogicalMods
            SET [Order] = @Order
            WHERE ProfileId = @ProfileId AND LogicalModId = @LogicalModId";

        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());
        command.Parameters.AddWithValue("@Order", order);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateEnabledComponentsAsync(Guid profileId, Guid logicalModId, List<Guid> enabledComponentIds, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE ProfileLogicalMods
            SET EnabledComponentIds = @EnabledComponentIds
            WHERE ProfileId = @ProfileId AND LogicalModId = @LogicalModId";

        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());
        command.Parameters.AddWithValue("@EnabledComponentIds", JsonSerializer.Serialize(enabledComponentIds));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid profileId, Guid logicalModId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProfileLogicalMods WHERE ProfileId = @ProfileId AND LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAllByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProfileLogicalMods WHERE ProfileId = @ProfileId";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid profileId, Guid logicalModId, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ProfileLogicalMods WHERE ProfileId = @ProfileId AND LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@ProfileId", profileId.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    private ProfileLogicalMod MapToProfileLogicalMod(SqliteDataReader reader)
    {
        var enabledComponentIdsJson = reader.GetString(reader.GetOrdinal("EnabledComponentIds"));
        var componentConfigurationsJson = reader.GetString(reader.GetOrdinal("ComponentConfigurations"));

        return new ProfileLogicalMod
        {
            ProfileId = Guid.Parse(reader.GetString(reader.GetOrdinal("ProfileId"))),
            LogicalModId = Guid.Parse(reader.GetString(reader.GetOrdinal("LogicalModId"))),
            Enabled = reader.GetBoolean(reader.GetOrdinal("Enabled")),
            Order = reader.GetInt32(reader.GetOrdinal("Order")),
            EnabledComponentIds = JsonSerializer.Deserialize<List<Guid>>(enabledComponentIdsJson) ?? new List<Guid>(),
            ComponentConfigurations = JsonSerializer.Deserialize<Dictionary<Guid, string>>(componentConfigurationsJson) ?? new Dictionary<Guid, string>()
        };
    }
}
