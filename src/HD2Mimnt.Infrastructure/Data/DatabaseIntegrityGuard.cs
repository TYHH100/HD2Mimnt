using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Data;

/// <summary>
/// 启动前的数据库完整性自检与抢救。
/// 检测到损坏时先备份现场，再按两级策略恢复：VACUUM INTO 重建 → 逐行探测抢救。
/// 恢复结果必须重新通过完整性校验才会替换现有文件，否则抛异常交由启动流程提示用户，
/// 避免后续每一次读写都以 SQLite Error 11 失败并弹出成排的错误通知。
/// </summary>
public static class DatabaseIntegrityGuard
{
    public sealed record Outcome(bool Repaired, string? BackupDirectory, string? Detail);

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    public static async Task<Outcome> EnsureHealthyAsync(string databasePath, ILogger? logger = null,
        CancellationToken cancellationToken = default, bool allowRepair = true)
    {
        if (!File.Exists(databasePath)) return new Outcome(false, null, null);
        if (await IsHealthyAsync(databasePath, cancellationToken)) return new Outcome(false, null, null);

        if (!allowRepair)
        {
            // 管理器主进程可能仍持有数据库，子进程不能替换文件，只报告给调用方处理。
            logger?.LogWarning("数据库完整性校验未通过，当前进程不执行修复: {Path}", databasePath);
            throw new InvalidOperationException("数据库文件完整性校验未通过。请退出管理器后重新启动以自动修复。");
        }

        logger?.LogWarning("数据库完整性校验未通过，开始尝试修复: {Path}", databasePath);
        var backupDirectory = Backup(databasePath);
        var directory = Path.GetDirectoryName(databasePath)!;

        // 一级：VACUUM INTO 按 B 树重新落盘。只修索引/空闲页问题，跨链页仍会保留。
        var vacuumed = Path.Combine(directory, $".repair-vacuum-{Guid.NewGuid():N}.db");
        try
        {
            VacuumInto(databasePath, vacuumed);
            if (await IsHealthyAsync(vacuumed, cancellationToken))
            {
                Install(vacuumed, databasePath);
                logger?.LogWarning("数据库已通过 VACUUM 重建，原文件备份于 {Backup}", backupDirectory);
                return new Outcome(true, backupDirectory, "已按页重建");
            }
            logger?.LogWarning("VACUUM 重建后校验仍未通过，改用逐行抢救");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "VACUUM 重建失败，改用逐行抢救");
        }
        finally
        {
            TryDelete(vacuumed);
        }

        // 二级：绕开受损 B 树逐行读取，重建一个结构完整的新库。
        var salvaged = Path.Combine(directory, $".repair-salvage-{Guid.NewGuid():N}.db");
        try
        {
            try
            {
                var detail = await SalvageAsync(databasePath, salvaged, logger, cancellationToken);
                if (await IsHealthyAsync(salvaged, cancellationToken))
                {
                    Install(salvaged, databasePath);
                    logger?.LogWarning("数据库已通过逐行抢救重建（{Detail}），原文件备份于 {Backup}", detail, backupDirectory);
                    return new Outcome(true, backupDirectory, detail);
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "逐行抢救失败");
            }
        }
        finally
        {
            TryDelete(salvaged);
        }

        throw new InvalidOperationException(
            $"数据库文件已损坏，自动修复未能得到一致的库。损坏文件已备份至：{backupDirectory}。" +
            "请在「设置 - 备份」中恢复备份后重启应用。");
    }

    public static async Task<bool> IsHealthyAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var healthy = true;
            var rows = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
                // 多行输出即表示存在问题，单行且为 ok 才是健康库。
                if (rows > 1 || !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                    healthy = false;
            }
            return healthy && rows == 1;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Backup(string databasePath)
    {
        // 同一秒内多次修复时秒级目录会互相覆盖，必须附带唯一后缀保留每次损坏现场。
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups",
            $"corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.Copy(databasePath, Path.Combine(directory, Path.GetFileName(databasePath)), true);
        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = databasePath + suffix;
            if (File.Exists(sidecar))
                File.Copy(sidecar, Path.Combine(directory, Path.GetFileName(sidecar)), true);
        }
        return directory;
    }

    private static void VacuumInto(string sourcePath, string targetPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO @Target";
        command.Parameters.AddWithValue("@Target", targetPath);
        command.ExecuteNonQuery();
    }

    private static void Install(string repairedPath, string databasePath)
    {
        // 连接池里可能还留着指向旧文件的句柄，替换前必须清空，否则新库会被旧句柄覆盖写回。
        SqliteConnection.ClearAllPools();

        // 先整体移开旧库与 WAL/SHM/JOURNAL，再放入新库：中途失败可原样恢复，
        // 避免出现“旧库还在但 WAL 已删”导致已提交数据丢失。
        var trash = databasePath + $".old-{Guid.NewGuid():N}";
        var moved = new List<(string From, string To)>();
        try
        {
            MoveAside(databasePath, trash, moved);
            foreach (var suffix in SidecarSuffixes)
                MoveAside(databasePath + suffix, trash + suffix, moved);
            File.Move(repairedPath, databasePath);
        }
        catch
        {
            TryDelete(databasePath);
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try { File.Move(moved[i].To, moved[i].From); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }

        // 新库已就位；旧文件此前已复制到备份目录，这里只清理改名后的残留。
        TryDelete(trash);
        foreach (var suffix in SidecarSuffixes) TryDelete(trash + suffix);
    }

    private static void MoveAside(string source, string target, List<(string From, string To)> moved)
    {
        if (!File.Exists(source)) return;
        File.Move(source, target);
        moved.Add((source, target));
    }

    private static async Task<string> SalvageAsync(string sourcePath, string targetPath,
        ILogger? logger, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken);
        await ExecuteAsync(source, "PRAGMA cell_size_check=ON", cancellationToken);

        var objects = await ReadSchemaAsync(source, cancellationToken);
        var virtualTables = objects
            .Where(o => o.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Name).ToList();
        bool IsShadow(string name) => virtualTables.Any(v =>
            name.StartsWith(v + "_", StringComparison.OrdinalIgnoreCase));
        var tables = objects.Where(o => o.Type == "table" && !IsShadow(o.Name)).ToList();
        var indexLike = objects.Where(o => o.Type is "index" or "trigger" && !IsShadow(o.Name)).ToList();

        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        await target.OpenAsync(cancellationToken);
        await ExecuteAsync(target, "PRAGMA journal_mode=OFF", cancellationToken);
        await ExecuteAsync(target, "PRAGMA synchronous=OFF", cancellationToken);
        await ExecuteAsync(target, "PRAGMA foreign_keys=OFF", cancellationToken);

        foreach (var table in tables) await ExecuteAsync(target, table.Sql, cancellationToken);

        var restored = 0;
        var dropped = 0;
        var unvisited = 0;
        foreach (var table in tables)
        {
            var columns = await ReadColumnsAsync(source, table.Name, cancellationToken);
            if (columns.Count == 0) continue;
            var (rows, rejected, missing) = await CopyTableAsync(source, target, table.Name, columns, cancellationToken);
            restored += rows;
            dropped += rejected;
            unvisited += missing;
            if (rejected > 0 || missing > 0)
                logger?.LogInformation("抢救 {Table}: 恢复 {Rows} 行，丢弃 {Rejected} 行，未扫描 {Missing} 行",
                    table.Name, rows, rejected, missing);
        }

        foreach (var item in indexLike)
        {
            try { await ExecuteAsync(target, item.Sql, cancellationToken); }
            catch (SqliteException ex) { logger?.LogWarning(ex, "重建 {Type} {Name} 失败", item.Type, item.Name); }
        }

        // FTS5 外部内容表的影子表不参与复制，按内容表重建索引。
        foreach (var name in virtualTables)
        {
            try { await ExecuteAsync(target, $"INSERT INTO \"{name.Replace("\"", "\"\"")}\"(\"{name.Replace("\"", "\"\"")}\") VALUES('rebuild')", cancellationToken); }
            catch (SqliteException ex) { logger?.LogWarning(ex, "重建全文索引 {Name} 失败", name); }
        }

        await ExecuteAsync(target, "PRAGMA journal_mode=WAL", cancellationToken);
        var detail = $"恢复 {restored} 行，丢弃 {dropped} 行";
        if (unvisited > 0) detail += $"，未扫描 {unvisited} 行";
        return detail;
    }

    private static async Task<(int Rows, int Rejected, int Unvisited)> CopyTableAsync(SqliteConnection source,
        SqliteConnection target, string table, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        var count = 0L;
        var maxRowId = 0L;
        try { count = Convert.ToInt64(await ScalarAsync(source, $"SELECT COUNT(*) FROM \"{table}\"", cancellationToken)); }
        catch (SqliteException) { }
        try { maxRowId = Convert.ToInt64(await ScalarAsync(source, $"SELECT MAX(rowid) FROM \"{table}\"", cancellationToken)); }
        catch (SqliteException) { }

        // 受损树的 MAX(rowid) 可能被遍历打断而偏小，只有不小于行数时才采信。
        var cap = count * 10 + 1000;
        if (maxRowId > 0 && maxRowId <= cap && maxRowId >= count) cap = maxRowId;
        cap = Math.Min(cap, 60000);

        var quoted = string.Join(",", columns.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
        var placeholders = string.Join(",", columns.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT OR REPLACE INTO \"{table.Replace("\"", "\"\"")}\" ({quoted}) VALUES ({placeholders})";
        var firstIsId = columns[0].Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                        columns[0].Equals("ModId", StringComparison.OrdinalIgnoreCase) ||
                        columns[0].Equals("ProfileId", StringComparison.OrdinalIgnoreCase);

        var rows = new List<object?[]>();
        for (long rowId = 1; rowId <= cap; rowId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var read = source.CreateCommand();
                read.CommandText = $"SELECT * FROM \"{table}\" WHERE rowid = @RowId";
                read.Parameters.AddWithValue("@RowId", rowId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) continue;
                var values = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }
            catch (SqliteException)
            {
                // 受损页读取失败：跳过该行，不影响其余数据。
            }
        }

        // 未读到的行：超出扫描上限、rowid 空洞或损坏页都算未扫描，必须在结果里如实报告。
        var unvisited = (int)Math.Min(int.MaxValue, Math.Max(0, count - rows.Count));

        // 首列是 GUID 主键时，多数值形如 GUID 才逐行校验，避免误伤 "default" 这类字符串主键。
        var guidChecked = firstIsId && rows.Count > 0 &&
                          rows.Count(r => r[0] is string s && Guid.TryParse(s, out _)) * 2 >= rows.Count;

        var restored = 0;
        var rejected = 0;
        using var transaction = target.BeginTransaction();
        foreach (var values in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guidChecked && !(values[0] is string text && Guid.TryParse(text, out _)))
            {
                // 受损页会把别的表的行串进本表，主键不像 GUID 的必然是串行。
                rejected++;
                continue;
            }
            try
            {
                await using var insert = target.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = insertSql;
                for (var i = 0; i < values.Length; i++)
                    insert.Parameters.AddWithValue($"@p{i}", values[i] ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                restored++;
            }
            catch (SqliteException)
            {
                rejected++;
            }
        }
        transaction.Commit();
        return (restored, rejected, unvisited);
    }

    private sealed record SchemaObject(string Type, string Name, string Sql);

    private static async Task<List<SchemaObject>> ReadSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<SchemaObject>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        // 先建表，再建索引与触发器：索引引用的列必须已经存在。
        return result.OrderBy(o => o.Type switch { "table" => 0, "index" => 1, _ => 2 }).ToList();
    }

    private static async Task<List<string>> ReadColumnsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info(@Table)";
        command.Parameters.AddWithValue("@Table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? 0 : value;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
