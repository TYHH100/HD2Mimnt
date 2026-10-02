using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository implementation for external tool management
/// </summary>
public class ExternalToolRepository : IExternalToolRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public ExternalToolRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<ExternalTool>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ExternalTools ORDER BY DisplayOrder, Name";

        var tools = new List<ExternalTool>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tools.Add(MapToExternalTool(reader));
        }

        return tools;
    }

    public async Task<List<ExternalTool>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ExternalTools WHERE IsEnabled = 1 ORDER BY DisplayOrder, Name";

        var tools = new List<ExternalTool>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tools.Add(MapToExternalTool(reader));
        }

        return tools;
    }

    public async Task<ExternalTool?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ExternalTools WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToExternalTool(reader);
        }

        return null;
    }

    public async Task<List<ExternalTool>> GetByExtensionAsync(string extension, CancellationToken cancellationToken = default)
    {
        // Normalize extension (ensure it starts with .)
        if (!extension.StartsWith("."))
        {
            extension = "." + extension;
        }

        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM ExternalTools
            WHERE IsEnabled = 1
            AND SupportedExtensions IS NOT NULL
            AND SupportedExtensions LIKE @Extension
            ORDER BY DisplayOrder, Name";
        command.Parameters.AddWithValue("@Extension", $"%{extension}%");

        var tools = new List<ExternalTool>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tools.Add(MapToExternalTool(reader));
        }

        return tools;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExternalTools WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    public async Task AddAsync(ExternalTool tool, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ExternalTools
            (Id, Name, Description, ExecutablePath, Arguments, WorkingDirectory, Icon,
             SupportedExtensions, IsEnabled, DisplayOrder, CreatedAt, ModifiedAt)
            VALUES
            (@Id, @Name, @Description, @ExecutablePath, @Arguments, @WorkingDirectory, @Icon,
             @SupportedExtensions, @IsEnabled, @DisplayOrder, @CreatedAt, @ModifiedAt)";

        AddParameters(command, tool);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(ExternalTool tool, CancellationToken cancellationToken = default)
    {
        tool.ModifiedAt = DateTime.UtcNow;

        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE ExternalTools SET
                Name = @Name,
                Description = @Description,
                ExecutablePath = @ExecutablePath,
                Arguments = @Arguments,
                WorkingDirectory = @WorkingDirectory,
                Icon = @Icon,
                SupportedExtensions = @SupportedExtensions,
                IsEnabled = @IsEnabled,
                DisplayOrder = @DisplayOrder,
                ModifiedAt = @ModifiedAt
            WHERE Id = @Id";

        AddParameters(command, tool);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ExternalTools WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ExternalTool MapToExternalTool(SqliteDataReader reader)
    {
        return new ExternalTool
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            ExecutablePath = reader.GetString(reader.GetOrdinal("ExecutablePath")),
            Arguments = reader.IsDBNull(reader.GetOrdinal("Arguments")) ? null : reader.GetString(reader.GetOrdinal("Arguments")),
            WorkingDirectory = reader.IsDBNull(reader.GetOrdinal("WorkingDirectory")) ? null : reader.GetString(reader.GetOrdinal("WorkingDirectory")),
            Icon = reader.IsDBNull(reader.GetOrdinal("Icon")) ? null : reader.GetString(reader.GetOrdinal("Icon")),
            SupportedExtensions = reader.IsDBNull(reader.GetOrdinal("SupportedExtensions")) ? null : reader.GetString(reader.GetOrdinal("SupportedExtensions")),
            IsEnabled = reader.GetInt32(reader.GetOrdinal("IsEnabled")) == 1,
            DisplayOrder = reader.GetInt32(reader.GetOrdinal("DisplayOrder")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ModifiedAt = reader.IsDBNull(reader.GetOrdinal("ModifiedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ModifiedAt")))
        };
    }

    private static void AddParameters(SqliteCommand command, ExternalTool tool)
    {
        command.Parameters.AddWithValue("@Id", tool.Id);
        command.Parameters.AddWithValue("@Name", tool.Name);
        command.Parameters.AddWithValue("@Description", (object?)tool.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExecutablePath", tool.ExecutablePath);
        command.Parameters.AddWithValue("@Arguments", (object?)tool.Arguments ?? DBNull.Value);
        command.Parameters.AddWithValue("@WorkingDirectory", (object?)tool.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("@Icon", (object?)tool.Icon ?? DBNull.Value);
        command.Parameters.AddWithValue("@SupportedExtensions", (object?)tool.SupportedExtensions ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEnabled", tool.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@DisplayOrder", tool.DisplayOrder);
        command.Parameters.AddWithValue("@CreatedAt", tool.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ModifiedAt", tool.ModifiedAt?.ToString("O") ?? (object)DBNull.Value);
    }
}
