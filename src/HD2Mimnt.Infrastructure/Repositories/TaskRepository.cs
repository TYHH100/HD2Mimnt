using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Task 仓储实现
/// </summary>
public class TaskRepository : ITaskRepository
{
    private readonly HD2MimntDbContext _context;

    public TaskRepository(HD2MimntDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 根据 ID 获取 Task
    /// </summary>
    public async Task<ModTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Type, Status, Progress, Stage, CurrentItem, Message,
                   StartTime, EndTime, Errors, Result
            FROM Tasks
            WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapFromReader(reader);
        }

        return null;
    }

    /// <summary>
    /// 获取所有 Task
    /// </summary>
    public async Task<IEnumerable<ModTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Type, Status, Progress, Stage, CurrentItem, Message,
                   StartTime, EndTime, Errors, Result
            FROM Tasks
            ORDER BY StartTime DESC";

        var tasks = new List<ModTask>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(MapFromReader(reader));
        }

        return tasks;
    }

    /// <summary>
    /// 获取所有运行中的 Task
    /// </summary>
    public async Task<IEnumerable<ModTask>> GetRunningTasksAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Type, Status, Progress, Stage, CurrentItem, Message,
                   StartTime, EndTime, Errors, Result
            FROM Tasks
            WHERE Status IN (@running, @waiting)
            ORDER BY StartTime ASC";
        command.Parameters.AddWithValue("@running", (int)Core.Entities.TaskStatus.Running);
        command.Parameters.AddWithValue("@waiting", (int)Core.Entities.TaskStatus.Waiting);

        var tasks = new List<ModTask>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(MapFromReader(reader));
        }

        return tasks;
    }

    /// <summary>
    /// 获取 Task 历史记录
    /// </summary>
    public async Task<IEnumerable<ModTask>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Name, Type, Status, Progress, Stage, CurrentItem, Message,
                   StartTime, EndTime, Errors, Result
            FROM Tasks
            WHERE Status IN (@completed, @failed, @partial, @cancelled, @terminated)
            ORDER BY COALESCE(EndTime, StartTime) DESC
            LIMIT @limit";
        command.Parameters.AddWithValue("@completed", (int)Core.Entities.TaskStatus.Completed);
        command.Parameters.AddWithValue("@failed", (int)Core.Entities.TaskStatus.Failed);
        command.Parameters.AddWithValue("@partial", (int)Core.Entities.TaskStatus.PartiallyFailed);
        command.Parameters.AddWithValue("@cancelled", (int)Core.Entities.TaskStatus.Cancelled);
        command.Parameters.AddWithValue("@terminated", (int)Core.Entities.TaskStatus.ForceTerminated);
        command.Parameters.AddWithValue("@limit", limit);

        var tasks = new List<ModTask>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(MapFromReader(reader));
        }

        return tasks;
    }

    /// <summary>
    /// 添加新 Task
    /// </summary>
    public async Task AddAsync(ModTask task, CancellationToken cancellationToken = default)
    {
        // 重试机制处理 SQLite 并发锁
        const int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                using var connection = await _context.OpenConnectionAsync(cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO Tasks (Id, Name, Type, Status, Progress, Stage, CurrentItem, Message,
                                      StartTime, EndTime, Errors, Result)
                    VALUES (@id, @name, @type, @status, @progress, @stage, @currentItem, @message,
                           @startTime, @endTime, @errors, @result)";

                AddParameters(command, task);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return; // 成功，退出重试循环
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && retry < maxRetries - 1) // SQLITE_BUSY
            {
                // 等待后重试
                await Task.Delay(100 * (retry + 1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 更新 Task
    /// </summary>
    public async Task UpdateAsync(ModTask task, CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Tasks
            SET Name = @name, Type = @type, Status = @status, Progress = @progress,
                Stage = @stage, CurrentItem = @currentItem, Message = @message,
                StartTime = @startTime, EndTime = @endTime, Errors = @errors, Result = @result
            WHERE Id = @id";

        AddParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 删除 Task
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _context.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Tasks WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, ModTask task)
    {
        command.Parameters.AddWithValue("@id", task.Id.ToString());
        command.Parameters.AddWithValue("@name", task.Name);
        command.Parameters.AddWithValue("@type", task.Type);
        command.Parameters.AddWithValue("@status", (int)task.Status);
        command.Parameters.AddWithValue("@progress", task.Progress);
        command.Parameters.AddWithValue("@stage", task.Stage ?? "");
        command.Parameters.AddWithValue("@currentItem", task.CurrentItem ?? "");
        command.Parameters.AddWithValue("@message", task.Message ?? "");
        command.Parameters.AddWithValue("@startTime", task.StartTime.ToString("O"));
        command.Parameters.AddWithValue("@endTime", task.EndTime?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@errors", JsonSerializer.Serialize(task.Errors));
        command.Parameters.AddWithValue("@result", task.Result ?? "");
    }

    private static ModTask MapFromReader(SqliteDataReader reader)
    {
        var errorsJson = reader.GetString(10);
        var errors = string.IsNullOrEmpty(errorsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(errorsJson) ?? new List<string>();

        return new ModTask
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Status = (Core.Entities.TaskStatus)reader.GetInt32(3),
            Progress = reader.GetDouble(4),
            Stage = reader.IsDBNull(5) ? "" : reader.GetString(5),
            CurrentItem = reader.IsDBNull(6) ? "" : reader.GetString(6),
            Message = reader.IsDBNull(7) ? "" : reader.GetString(7),
            StartTime = DateTime.Parse(reader.GetString(8)),
            EndTime = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
            Errors = errors,
            Result = reader.IsDBNull(11) ? "" : reader.GetString(11)
        };
    }
}
