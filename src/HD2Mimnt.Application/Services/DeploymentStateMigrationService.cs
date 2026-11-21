using System.Globalization;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 把旧会话式部署记录收养为“当前安装状态”：推断 GamePath、合并同一安装同一模组的
/// 多会话记录、清理孤儿文件行。只移动数据库归属，不删除磁盘文件。
/// </summary>
public sealed class DeploymentStateMigrationService(
    HD2MimntDbContext dbContext,
    ILogger<DeploymentStateMigrationService>? logger = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public sealed record Result(int AdoptedRecords, int MergedRecords, int RemovedOrphanFiles, int AmbiguousRecords,
        int RemovedCorruptRows);

    public async Task<Result> EnsureMigratedAsync(string? effectiveGamePath = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = await dbContext.OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            var (records, removedRecordRows) = await ReadRecordsAsync(connection, transaction, logger, cancellationToken);
            var (files, removedFileRows) = await ReadFilesAsync(connection, transaction, logger, cancellationToken);
            var filesByDeployment = files.GroupBy(file => file.DeploymentId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var normalizedGamePath = NormalizePathOrNull(effectiveGamePath);
            var adopted = 0;
            var ambiguous = 0;

            // 1. 旧记录归属：优先从目标路径推断安装根目录，推断失败时用当前生效目录兜底。
            foreach (var record in records)
            {
                var current = NormalizePathOrNull(record.GamePath);
                if (current != null)
                {
                    if (!current.Equals(record.GamePath, StringComparison.Ordinal))
                    {
                        record.GamePath = current;
                        await UpdateRecordGamePathAsync(connection, transaction, record.Id, current, cancellationToken);
                    }
                    foreach (var file in filesByDeployment.GetValueOrDefault(record.Id) ?? [])
                    {
                        if (string.Equals(NormalizePathOrNull(file.GamePath), current, StringComparison.OrdinalIgnoreCase))
                            continue;
                        file.GamePath = current;
                        await UpdateFileGamePathAsync(connection, transaction, file.Id, current, cancellationToken);
                    }
                    continue;
                }

                var recordFiles = filesByDeployment.GetValueOrDefault(record.Id) ?? [];
                var inferred = InferSingleRoot(recordFiles);
                var candidate = inferred ?? (normalizedGamePath != null
                    && recordFiles.All(file => IsUnderRoot(file.TargetPath, normalizedGamePath))
                        ? normalizedGamePath
                        : null);
                if (candidate == null && recordFiles.Count == 0)
                    candidate = normalizedGamePath;

                if (candidate == null)
                {
                    ambiguous++;
                    continue;
                }

                record.GamePath = candidate;
                adopted++;
                await UpdateRecordGamePathAsync(connection, transaction, record.Id, candidate, cancellationToken);
                foreach (var file in recordFiles)
                {
                    file.GamePath = candidate;
                    await UpdateFileGamePathAsync(connection, transaction, file.Id, candidate, cancellationToken);
                }
            }

            // 2. 同一 (GamePath, ModId) 只保留最新一条，其余文件按目标路径并入。
            var merged = 0;
            foreach (var group in records
                         .Where(record => !string.IsNullOrWhiteSpace(record.GamePath))
                         .GroupBy(record => (GamePath: record.GamePath!.ToUpperInvariant(), record.ModId)))
            {
                var ordered = group.OrderByDescending(record => record.StartTime)
                    .ThenByDescending(record => record.AppliedAt ?? record.EndTime ?? DateTime.MinValue)
                    .ThenByDescending(record => record.Id)
                    .ToList();
                var keep = ordered[0];
                var keptFiles = filesByDeployment.GetValueOrDefault(keep.Id) ?? [];
                var keptTargets = keptFiles.Select(file => NormalizePathOrNull(file.TargetPath))
                    .Where(path => path != null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var duplicate in ordered.Skip(1))
                {
                    merged++;
                    foreach (var file in filesByDeployment.GetValueOrDefault(duplicate.Id) ?? [])
                    {
                        var target = NormalizePathOrNull(file.TargetPath);
                        if (target != null && keptTargets.Add(target))
                        {
                            file.DeploymentId = keep.Id;
                            file.GamePath = keep.GamePath;
                            await MoveFileToDeploymentAsync(connection, transaction, file.Id, keep.Id, keep.GamePath!, cancellationToken);
                        }
                        else
                        {
                            await DeleteFileAsync(connection, transaction, file.Id, cancellationToken);
                        }
                    }

                    await DeleteRecordAsync(connection, transaction, duplicate.Id, cancellationToken);
                }

                // 同一安装内的目标路径必须唯一；历史脏数据保留最新一条目标记录。
                foreach (var file in keptFiles
                             .GroupBy(file => NormalizePathOrNull(file.TargetPath), StringComparer.OrdinalIgnoreCase)
                             .Where(pair => pair.Key != null && pair.Count() > 1))
                {
                    foreach (var duplicate in file.Skip(1))
                        await DeleteFileAsync(connection, transaction, duplicate.Id, cancellationToken);
                }
            }

            // 3. 旧流程漏删的 DeploymentFiles 孤儿行（SQLite 外键级联未启用）。
            var orphans = 0;
            using (var orphanCommand = connection.CreateCommand())
            {
                orphanCommand.Transaction = transaction;
                orphanCommand.CommandText = @"
                    DELETE FROM DeploymentFiles
                    WHERE DeploymentId NOT IN (SELECT Id FROM DeploymentRecords)";
                orphans = await orphanCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // 4. 当前状态唯一约束；老库若仍有异常重复只记录警告，不阻断启动。
            // NOCASE 与 Upsert 查重（GamePath = @GamePath COLLATE NOCASE）一致：
            // 游戏路径大小写变化时不会再因 BINARY 索引漏查而插入两行“当前状态”。
            try
            {
                using var index = connection.CreateCommand();
                index.Transaction = transaction;
                index.CommandText = @"
                    DROP INDEX IF EXISTS idx_deployment_gamepath_mod_unique;
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_deployment_gamepath_mod_unique_nocase
                    ON DeploymentRecords(GamePath COLLATE NOCASE, ModId) WHERE GamePath IS NOT NULL";
                await index.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex)
            {
                logger?.LogWarning(ex, "创建部署当前状态唯一索引失败，可能存在未归属的重复记录");
            }

            await transaction.CommitAsync(cancellationToken);
            var result = new Result(adopted, merged, orphans, ambiguous, removedRecordRows + removedFileRows);
            if (adopted + merged + orphans + ambiguous + result.RemovedCorruptRows > 0)
                logger?.LogInformation("部署状态迁移完成: 归属 {Adopted}, 合并 {Merged}, 孤儿文件 {Orphans}, 未识别 {Ambiguous}, 清理坏行 {Corrupt}",
                    adopted, merged, orphans, ambiguous, result.RemovedCorruptRows);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(List<DeploymentRecord> Items, int Removed)> ReadRecordsAsync(SqliteConnection connection,
        SqliteTransaction transaction, ILogger? logger, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT rowid, Id, SessionId, ProfileId, ModId, GamePath, StartTime, EndTime, AppliedAt
            FROM DeploymentRecords";
        var records = new List<DeploymentRecord>();
        var brokenRowIds = new List<long>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                // 库文件受损时列会错位，读到的不一定是合法 GUID/时间；坏行无法按 Id 寻址，直接清理。
                if (!TryGuid(reader, 1, out var id) || !TryGuid(reader, 2, out var sessionId) ||
                    !TryGuid(reader, 3, out var profileId) || !TryGuid(reader, 4, out var modId) ||
                    !TryTimestamp(reader, 6, out var startTime))
                {
                    brokenRowIds.Add(reader.GetInt64(0));
                    continue;
                }

                records.Add(new DeploymentRecord
                {
                    Id = id,
                    SessionId = sessionId,
                    ProfileId = profileId,
                    ModId = modId,
                    GamePath = reader.IsDBNull(5) ? null : reader.GetValue(5) as string,
                    StartTime = startTime,
                    EndTime = TryTimestamp(reader, 7, out var endTime) ? endTime : null,
                    AppliedAt = TryTimestamp(reader, 8, out var appliedAt) ? appliedAt : null
                });
            }
        }

        var removed = await DeleteBrokenRowsAsync(connection, transaction, "DeploymentRecords", brokenRowIds,
            cancellationToken);
        if (removed > 0)
            logger?.LogWarning("已清理 {Count} 条无法解析的部署记录（主键或时间字段异常，疑似数据库损坏）", removed);
        return (records, removed);
    }

    private static async Task<(List<DeploymentFile> Items, int Removed)> ReadFilesAsync(SqliteConnection connection,
        SqliteTransaction transaction, ILogger? logger, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT rowid, Id, DeploymentId, ModId, TargetPath, GamePath FROM DeploymentFiles";
        var files = new List<DeploymentFile>();
        var brokenRowIds = new List<long>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!TryGuid(reader, 1, out var id) || !TryGuid(reader, 2, out var deploymentId) ||
                    !TryGuid(reader, 3, out var modId) || reader.GetValue(4) is not string targetPath)
                {
                    brokenRowIds.Add(reader.GetInt64(0));
                    continue;
                }

                files.Add(new DeploymentFile
                {
                    Id = id,
                    DeploymentId = deploymentId,
                    ModId = modId,
                    TargetPath = targetPath,
                    GamePath = reader.IsDBNull(5) ? null : reader.GetValue(5) as string
                });
            }
        }

        var removed = await DeleteBrokenRowsAsync(connection, transaction, "DeploymentFiles", brokenRowIds,
            cancellationToken);
        if (removed > 0)
            logger?.LogWarning("已清理 {Count} 条无法解析的部署文件行（主键或路径字段异常，疑似数据库损坏）", removed);
        return (files, removed);
    }

    /// <summary>坏行主键不可解析，按 Id 的读写永远无法命中；留在表里只会反复让严格读取失败。</summary>
    private static async Task<int> DeleteBrokenRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, IReadOnlyList<long> rowIds, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var chunk in rowIds.Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var placeholders = string.Join(",", chunk.Select((_, index) => $"@p{index}"));
            command.CommandText = $"DELETE FROM {table} WHERE rowid IN ({placeholders})";
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return removed;
    }

    private static bool TryGuid(SqliteDataReader reader, int ordinal, out Guid value)
    {
        value = default;
        if (reader.IsDBNull(ordinal)) return false;
        return reader.GetValue(ordinal) is string text && Guid.TryParse(text, out value);
    }

    private static bool TryTimestamp(SqliteDataReader reader, int ordinal, out DateTime value)
    {
        value = default;
        if (reader.IsDBNull(ordinal)) return false;
        return reader.GetValue(ordinal) is string text &&
               DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    /// <summary>所有目标必须归属同一个安装根目录；混合安装的旧记录保持未归属。</summary>
    private static string? InferSingleRoot(IReadOnlyList<DeploymentFile> files)
    {
        string? root = null;
        foreach (var file in files)
        {
            if (!TryInferRoot(file.TargetPath, out var candidate))
                return null;
            if (root == null) root = candidate;
            else if (!root.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return root;
    }

    internal static bool TryInferRoot(string targetPath, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        string full;
        try { full = Path.GetFullPath(targetPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) return false;

        // 普通补丁直接位于 <game>/data/ 下。
        if (Path.GetFileName(directory).Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent))
            {
                root = Path.TrimEndingDirectorySeparator(parent);
                return true;
            }
        }

        // HD2IAS 原生文件位于 <game>/bin/HD2IndependentRig/ 下。
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!Path.GetFileName(current).Equals("HD2IndependentRig", StringComparison.OrdinalIgnoreCase))
                continue;
            var bin = Path.GetDirectoryName(current);
            if (bin == null || !Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase))
                continue;
            var parent = Path.GetDirectoryName(bin);
            if (!string.IsNullOrEmpty(parent))
            {
                root = Path.TrimEndingDirectorySeparator(parent);
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task UpdateRecordGamePathAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string gamePath, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE DeploymentRecords SET GamePath = @GamePath WHERE Id = @Id";
        command.Parameters.AddWithValue("@GamePath", gamePath);
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFileGamePathAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string gamePath, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE DeploymentFiles SET GamePath = @GamePath WHERE Id = @Id";
        command.Parameters.AddWithValue("@GamePath", gamePath);
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MoveFileToDeploymentAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid fileId, Guid deploymentId, string gamePath, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            UPDATE DeploymentFiles
            SET DeploymentId = @DeploymentId, GamePath = @GamePath
            WHERE Id = @Id";
        command.Parameters.AddWithValue("@DeploymentId", deploymentId.ToString());
        command.Parameters.AddWithValue("@GamePath", gamePath);
        command.Parameters.AddWithValue("@Id", fileId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteFileAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM DeploymentFiles WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteRecordAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM DeploymentRecords WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
