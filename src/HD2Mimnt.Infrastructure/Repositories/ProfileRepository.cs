using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Profile Repository 实现
/// </summary>
public class ProfileRepository : IProfileRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public ProfileRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var profile = MapToProfile(reader);
            await LoadProfileModsAsync(profile, cancellationToken);
            return profile;
        }

        return null;
    }

    public async Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles ORDER BY SortOrder, CreatedAt";

        var profiles = new List<Profile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var profile = MapToProfile(reader);
            profiles.Add(profile);
        }

        foreach (var profile in profiles)
        {
            await LoadProfileModsAsync(profile, cancellationToken);
        }

        return profiles;
    }

    public async Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE IsActive = 1 LIMIT 1";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var profile = MapToProfile(reader);
            await LoadProfileModsAsync(profile, cancellationToken);
            return profile;
        }

        return null;
    }

    public async Task AddAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO Profiles (Id, Name, Description, CreatedAt, ModifiedAt, IsActive, SortOrder)
                VALUES (@Id, @Name, @Description, @CreatedAt, @ModifiedAt, @IsActive, @SortOrder)";

            AddProfileParameters(command, profile);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await SaveProfileModsAsync(profile, connection, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                UPDATE Profiles
                SET Name = @Name, Description = @Description, ModifiedAt = @ModifiedAt, IsActive = @IsActive, SortOrder = @SortOrder
                WHERE Id = @Id";

            AddProfileParameters(command, profile);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // 删除旧的 ProfileMods
            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM ProfileMods WHERE ProfileId = @ProfileId";
            deleteCommand.Parameters.AddWithValue("@ProfileId", profile.Id.ToString());
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

            await SaveProfileModsAsync(profile, connection, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Profiles WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Profile> DuplicateAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        var original = await GetByIdAsync(id, cancellationToken);
        if (original == null)
        {
            throw new InvalidOperationException($"Profile {id} not found");
        }

        var duplicate = new Profile
        {
            Id = Guid.NewGuid(),
            Name = newName,
            Description = original.Description,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            IsActive = false,
            SortOrder = await GetNextSortOrderAsync(cancellationToken),
            Mods = original.Mods.Select(m => new ProfileMod
            {
                ProfileId = Guid.Empty, // Will be set when adding
                ModId = m.ModId,
                Enabled = m.Enabled,
                Order = m.Order,
                OptionsJson = m.OptionsJson
            }).ToList()
        };

        // Update ProfileId for each ProfileMod
        foreach (var mod in duplicate.Mods)
        {
            mod.ProfileId = duplicate.Id;
        }

        await AddAsync(duplicate, cancellationToken);
        return duplicate;
    }

    public async Task SetActiveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            // 先取消所有激活状态
            using var deactivateCommand = connection.CreateCommand();
            deactivateCommand.Transaction = transaction;
            deactivateCommand.CommandText = "UPDATE Profiles SET IsActive = 0";
            await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);

            // 激活指定 Profile
            using var activateCommand = connection.CreateCommand();
            activateCommand.Transaction = transaction;
            activateCommand.CommandText = "UPDATE Profiles SET IsActive = 1 WHERE Id = @Id";
            activateCommand.Parameters.AddWithValue("@Id", id.ToString());
            await activateCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task LoadProfileModsAsync(Profile profile, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT ProfileId, ModId, Enabled, [Order], OptionsJson
            FROM ProfileMods
            WHERE ProfileId = @ProfileId
            ORDER BY [Order]";
        command.Parameters.AddWithValue("@ProfileId", profile.Id.ToString());

        profile.Mods.Clear();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profile.Mods.Add(new ProfileMod
            {
                ProfileId = Guid.Parse(reader.GetString(0)),
                ModId = Guid.Parse(reader.GetString(1)),
                Enabled = reader.GetBoolean(2),
                Order = reader.GetInt32(3),
                OptionsJson = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
            });
        }
    }

    private async Task SaveProfileModsAsync(Profile profile, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var mod in profile.Mods)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order], OptionsJson)
                VALUES (@ProfileId, @ModId, @Enabled, @Order, @OptionsJson)";

            command.Parameters.AddWithValue("@ProfileId", profile.Id.ToString());
            command.Parameters.AddWithValue("@ModId", mod.ModId.ToString());
            command.Parameters.AddWithValue("@Enabled", mod.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@Order", mod.Order);
            command.Parameters.AddWithValue("@OptionsJson", mod.OptionsJson ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static Profile MapToProfile(SqliteDataReader reader)
    {
        return new Profile
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("Description")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ModifiedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ModifiedAt"))),
            IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
            SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder"))
        };
    }

    /// <summary>下一个可用排序号（追加到列表末尾）。</summary>
    private async Task<int> GetNextSortOrderAsync(CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Profiles";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static void AddProfileParameters(SqliteCommand command, Profile profile)
    {
        command.Parameters.AddWithValue("@Id", profile.Id.ToString());
        command.Parameters.AddWithValue("@Name", profile.Name);
        command.Parameters.AddWithValue("@Description", profile.Description);
        command.Parameters.AddWithValue("@CreatedAt", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ModifiedAt", profile.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("@IsActive", profile.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("@SortOrder", profile.SortOrder);
    }
}
