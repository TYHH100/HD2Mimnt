using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// SQLite implementation of IPluginExecutionRepository
/// </summary>
public class PluginExecutionRepository : IPluginExecutionRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public PluginExecutionRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<PluginExecutionHistory>> GetByPluginIdAsync(string pluginId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, PluginId, Command, StartTime, EndTime, Status, Result, Error
            FROM PluginExecutionHistory
            WHERE PluginId = @PluginId
            ORDER BY StartTime DESC
            LIMIT @Limit";
        command.Parameters.AddWithValue("@PluginId", pluginId);
        command.Parameters.AddWithValue("@Limit", limit);

        var executions = new List<PluginExecutionHistory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            executions.Add(MapExecution(reader));
        }

        return executions;
    }

    public async Task AddAsync(PluginExecutionHistory execution, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PluginExecutionHistory (Id, PluginId, Command, StartTime, EndTime, Status, Result, Error)
            VALUES (@Id, @PluginId, @Command, @StartTime, @EndTime, @Status, @Result, @Error)";

        command.Parameters.AddWithValue("@Id", execution.Id);
        command.Parameters.AddWithValue("@PluginId", execution.PluginId);
        command.Parameters.AddWithValue("@Command", execution.Command);
        command.Parameters.AddWithValue("@StartTime", execution.StartTime.ToString("O"));
        command.Parameters.AddWithValue("@EndTime", execution.EndTime?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", execution.Status);
        command.Parameters.AddWithValue("@Result", execution.Result ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Error", execution.Error ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(PluginExecutionHistory execution, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE PluginExecutionHistory
            SET Command = @Command, StartTime = @StartTime, EndTime = @EndTime,
                Status = @Status, Result = @Result, Error = @Error
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", execution.Id);
        command.Parameters.AddWithValue("@Command", execution.Command);
        command.Parameters.AddWithValue("@StartTime", execution.StartTime.ToString("O"));
        command.Parameters.AddWithValue("@EndTime", execution.EndTime?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", execution.Status);
        command.Parameters.AddWithValue("@Result", execution.Result ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Error", execution.Error ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PluginExecutionHistory MapExecution(SqliteDataReader reader)
    {
        return new PluginExecutionHistory
        {
            Id = reader.GetString(0),
            PluginId = reader.GetString(1),
            Command = reader.GetString(2),
            StartTime = DateTime.Parse(reader.GetString(3)),
            EndTime = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
            Status = reader.GetString(5),
            Result = reader.IsDBNull(6) ? null : reader.GetString(6),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }
}
