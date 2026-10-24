using System.Text.Json;
using HD2Mimnt.Core.Entities;
using Microsoft.Data.Sqlite;
using static HD2Mimnt.Infrastructure.Storage.StorageLocationStore;

namespace HD2Mimnt.Infrastructure.Storage;

/// <summary>启动期搬迁。复制完成才发布定位文件；提交日志使中断后的启动可以继续提交。</summary>
public sealed class StorageMigration(StorageLocationStore store)
{
    private const string Marker = ".hd2mimnt-migration";
    private StorageLocations Current => store.State.Current;

    public StorageLocations Validate(StorageLocations requested)
    {
        var next = new StorageLocations(Normalize(requested.AppDataPath), Normalize(requested.ModsPath), Normalize(requested.CachePath));
        if (Contains(next.ModsPath, next.AppDataPath) || Contains(next.CachePath, next.AppDataPath) ||
            Contains(next.ModsPath, next.CachePath) || Contains(next.CachePath, next.ModsPath))
            throw new ArgumentException("应用数据、模组库与缓存不能使用同一个目录；模组库和缓存也不能互相包含。");
        var moves = GetMoves(next);
        foreach (var move in moves)
        {
            // 禁止源/目标互相包含及交换位置；允许新的库/缓存位于未搬迁的应用数据目录下。
            foreach (var source in new[] { Current.AppDataPath, Current.ModsPath, Current.CachePath })
            {
                if (Same(source, Current.AppDataPath) && Same(Current.AppDataPath, next.AppDataPath)) continue;
                if (Contains(source, move.Target) || Contains(move.Target, source))
                    throw new ArgumentException("新位置不能与正在使用的存储目录重叠，请选择独立的空目录。");
            }
            CheckDestination(move.Target);
        }
        return next;
    }

    private void CheckDestination(string path)
    {
        RejectLinkedAncestors(path);
        if (File.Exists(path)) throw new ArgumentException("目标位置是文件，请选择目录。");
        var existed = Directory.Exists(path);
        if (existed && Directory.EnumerateFileSystemEntries(path).Any(entry => !store.IsBootstrapFile(entry)))
            throw new ArgumentException("新位置必须是空目录，以免覆盖已有文件。");
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".hd2mimnt-write-" + Guid.NewGuid().ToString("N"));
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (!existed && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
    }

    public async Task CheckLinkedDeploymentsAsync(StorageLocations next, CancellationToken token)
    {
        if (Same(Current.ModsPath, next.ModsPath) || !File.Exists(Current.DatabasePath)) return;
        await using var connection = Open(Current.DatabasePath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(token).ConfigureAwait(false);
        if (!await HasColumnAsync(connection, "DeploymentFiles", "Method", token)) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM DeploymentFiles f
            JOIN DeploymentRecords r ON r.Id = f.DeploymentId
            WHERE COALESCE(NULLIF(f.Method, ''), r.DeploymentMethod) = 'SymbolicLink'
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
            throw new InvalidOperationException("模组仍有符号链接部署，请先在配置文件页清理部署，再修改模组库位置。");
    }

    public async Task<string?> ApplyAsync(CancellationToken token)
    {
        if (store.State.Pending == null) return null;
        // 先把旧版缓存收拢到当前缓存根，再执行用户的跨盘搬迁。
        foreach (var name in new[] { "ImageCache", "Downloads" })
        {
            var legacy = Path.Combine(Current.AppDataPath, name);
            var target = Path.Combine(Current.CachePath, name);
            if (!Directory.Exists(legacy) || Same(legacy, target)) continue;
            Directory.CreateDirectory(Current.CachePath);
            if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
            if (!Directory.Exists(target)) Directory.Move(legacy, target);
            else if (Directory.EnumerateFileSystemEntries(legacy).Any())
                throw new IOException("旧缓存与新缓存同时有文件，请检查目录后重试：" + legacy);
        }
        if (store.State.Pending is { } next)
        {
            if (!store.State.ReadyToCommit)
            {
                var moves = GetMoves(next);
                var id = store.State.MigrationId!;
                try
                {
                    // 上次在复制阶段退出：只清理带本次标记的目标，源目录始终保持完整。
                    RollbackCopies(moves, id);
                    DeleteScratch(next, id);
                    Validate(next);
                    await CheckLinkedDeploymentsAsync(next, token).ConfigureAwait(false);
                    await CheckpointAsync(token).ConfigureAwait(false);
                    var cleanup = new List<string>(store.State.CleanupFiles);
                    foreach (var move in moves)
                    {
                        Directory.CreateDirectory(move.Target);
                        File.WriteAllText(Path.Combine(move.Target, Marker), id);
                    }
                    foreach (var move in moves)
                    {
                        await CopyTreeAsync(move.Source, move.Target, cleanup, token).ConfigureAwait(false);
                    }
                    var scratch = Scratch(next, id);
                    Directory.CreateDirectory(scratch);
                    var references = new List<StorageMove>
                    {
                        new(Current.AppDataPath, next.AppDataPath), new(Current.ModsPath, next.ModsPath),
                        new(Current.CachePath, next.CachePath)
                    };
                    if (File.Exists(Current.DatabasePath))
                    {
                        var snapshot = Path.Combine(scratch, "hd2mimnt.db");
                        await SnapshotAsync(Current.DatabasePath, snapshot, token).ConfigureAwait(false);
                        await RewriteDatabaseAsync(snapshot, references, token).ConfigureAwait(false);
                        if (!Same(Current.AppDataPath, next.AppDataPath))
                        {
                            cleanup.Add(Current.DatabasePath + "-wal");
                            cleanup.Add(Current.DatabasePath + "-shm");
                        }
                    }
                    await RewriteSettingsAsync(scratch, references, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await store.SaveAsync(store.State with
                    {
                        Moves = moves, CleanupFiles = cleanup, ReadyToCommit = true
                    }, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!store.State.ReadyToCommit)
                {
                    RollbackCopies(moves, id);
                    DeleteScratch(next, id);
                    if (ex is OperationCanceledException) throw;
                    return "存储位置迁移失败，仍使用原位置：" + ex.Message;
                }
            }
            // ReadyToCommit 之后不接受取消；这段只发布小文件，失败时保留日志供下次启动重试。
            var candidate = Path.Combine(Scratch(next, store.State.MigrationId!), "hd2mimnt.db");
            if (File.Exists(candidate))
            {
                Directory.CreateDirectory(next.AppDataPath);
                File.Copy(candidate, next.DatabasePath + ".storage-new", true);
                File.Move(next.DatabasePath + ".storage-new", next.DatabasePath, true);
                File.Delete(next.DatabasePath + "-wal");
                File.Delete(next.DatabasePath + "-shm");
            }
            var candidateSettings = Path.Combine(Scratch(next, store.State.MigrationId!), "settings.json");
            if (File.Exists(candidateSettings))
            {
                File.Copy(candidateSettings, Path.Combine(next.AppDataPath, "settings.json.storage-new"), true);
                File.Move(Path.Combine(next.AppDataPath, "settings.json.storage-new"), Path.Combine(next.AppDataPath, "settings.json"), true);
            }
            await store.SaveAsync(store.State with { Current = next, Pending = null, ReadyToCommit = false }).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Current.AppDataPath);
        Directory.CreateDirectory(Current.ModsPath);
        Directory.CreateDirectory(Current.TempPath);
        return await CleanupCommittedAsync().ConfigureAwait(false);
    }

    /// <summary>仅恢复已提交搬迁留下的清理，不创建目录或执行新的搬迁。</summary>
    public async Task<string?> CleanupCommittedAsync()
    {
        if (store.State.Pending != null ||
            (store.State.MigrationId == null && store.State.CleanupFiles.Count == 0)) return null;
        if (store.State.MigrationId is { } migrationId)
        {
            foreach (var move in store.State.Moves)
                if (Owns(move.Target, migrationId)) File.Delete(Path.Combine(move.Target, Marker));
            DeleteScratch(Current, migrationId);
        }
        var remaining = new List<string>();
        foreach (var file in store.State.CleanupFiles)
        {
            try
            {
                File.Delete(file);
                RemoveEmptyParents(Path.GetDirectoryName(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { remaining.Add(file); }
        }
        await store.SaveAsync(new(Current) { CleanupFiles = remaining }).ConfigureAwait(false);
        return remaining.Count == 0 ? null : "存储位置已切换，部分旧文件未能删除，下次启动将重试清理。";
    }

    private List<StorageMove> GetMoves(StorageLocations next)
    {
        var moves = new List<StorageMove>();
        if (!Same(Current.AppDataPath, next.AppDataPath)) moves.Add(new(Current.AppDataPath, next.AppDataPath));
        if (!Same(Current.ModsPath, next.ModsPath)) moves.Add(new(Current.ModsPath, next.ModsPath));
        if (!Same(Current.CachePath, next.CachePath)) moves.Add(new(Current.CachePath, next.CachePath));
        return moves;
    }

    private async Task CopyTreeAsync(string source, string target, List<string> cleanup, CancellationToken token)
    {
        if (!Directory.Exists(source)) return;
        RejectLinkedAncestors(source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            if (store.IsBootstrapFile(entry) || Same(entry, Current.ModsPath) || Same(entry, Current.CachePath)) continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存储目录中包含链接，不能自动迁移：" + entry);
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (File.Exists(Path.Combine(destination, Marker)))
                    throw new IOException("新的子目录与应用数据中已有的目录冲突：" + destination);
                Directory.CreateDirectory(destination);
                await CopyTreeAsync(entry, destination, cleanup, token).ConfigureAwait(false);
            }
            else
            {
                if (entry.EndsWith("hd2mimnt.db-wal", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith("hd2mimnt.db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                cleanup.Add(entry);
            }
        }
    }

    private async Task CheckpointAsync(CancellationToken token)
    {
        if (!File.Exists(Current.DatabasePath)) return;
        await using var connection = Open(Current.DatabasePath, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (await reader.ReadAsync(token).ConfigureAwait(false) && reader.GetInt32(0) != 0)
            throw new IOException("数据库仍被其他程序占用，请关闭相关程序后重试。");
    }

    private static async Task SnapshotAsync(string source, string target, CancellationToken token)
    {
        File.Delete(target);
        await using var input = Open(source, SqliteOpenMode.ReadOnly);
        await using var output = Open(target, SqliteOpenMode.ReadWriteCreate);
        await input.OpenAsync(token).ConfigureAwait(false);
        await output.OpenAsync(token).ConfigureAwait(false);
        input.BackupDatabase(output);
    }

    private static async Task RewriteDatabaseAsync(string database, List<StorageMove> moves, CancellationToken token)
    {
        await using var connection = Open(database, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var (table, column) in new[]
        {
            ("Mods", "Path"), ("Mods", "ThumbnailPath"), ("DeploymentFiles", "SourcePath"),
            ("LogicalMods", "ThumbnailPath"), ("PackageSources", "PhysicalPath"), ("DeployableFiles", "SourcePath"),
            ("ArchiveAnalysisCache", "FilePath"), ("Plugins", "ManifestPath"), ("Plugins", "AssemblyPath"),
            ("ExternalTools", "ExecutablePath")
        })
        {
            if (!await HasColumnAsync(connection, table, column, token, transaction)) continue;
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT rowid, [{column}] FROM [{table}] WHERE [{column}] IS NOT NULL";
            var updates = new List<(long Id, string Path)>();
            await using (var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var path = reader.GetString(1);
                    var mapped = Map(path, moves);
                    if (mapped != path) updates.Add((reader.GetInt64(0), mapped));
                }
            foreach (var update in updates)
            {
                using var write = connection.CreateCommand();
                write.Transaction = transaction;
                write.CommandText = $"UPDATE [{table}] SET [{column}] = $path WHERE rowid = $id";
                write.Parameters.AddWithValue("$path", update.Path);
                write.Parameters.AddWithValue("$id", update.Id);
                await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        if (await HasColumnAsync(connection, "Settings", "Value", token, transaction))
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT Value FROM Settings WHERE Key = 'UI.BackgroundImagePath'";
            if (await read.ExecuteScalarAsync(token).ConfigureAwait(false) is string json)
            {
                var value = JsonSerializer.Deserialize<JsonElement>(json);
                if (value.ValueKind == JsonValueKind.String)
                {
                    using var write = connection.CreateCommand();
                    write.Transaction = transaction;
                    write.CommandText = "UPDATE Settings SET Value = $value WHERE Key = 'UI.BackgroundImagePath'";
                    write.Parameters.AddWithValue("$value", JsonSerializer.Serialize(Map(value.GetString()!, moves)));
                    await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
        // 设置现存于 SQLite，路径类设置仍是 JSON 字符串；只映射本应用管理的根目录。
        if (await HasColumnAsync(connection, "Settings", "Value", token, transaction))
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT Value FROM Settings WHERE Key = 'UI.BackgroundImagePath'";
            if (await read.ExecuteScalarAsync(token).ConfigureAwait(false) is string json)
            {
                var value = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    using var write = connection.CreateCommand();
                    write.Transaction = transaction;
                    write.CommandText = "UPDATE Settings SET Value = $value WHERE Key = 'UI.BackgroundImagePath'";
                    write.Parameters.AddWithValue("$value", JsonSerializer.Serialize(Map(value, moves)));
                    await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
        transaction.Commit();
    }

    private async Task RewriteSettingsAsync(string scratch, List<StorageMove> moves, CancellationToken token)
    {
        var path = Path.Combine(Current.AppDataPath, "settings.json");
        if (!File.Exists(path)) return;
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        if (settings == null) throw new InvalidDataException("设置文件无效。");
        foreach (var key in new[] { "UI.BackgroundImagePath" })
            if (settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
                settings[key] = JsonSerializer.SerializeToElement(Map(value.GetString()!, moves));
        await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);
    }

    private static string Map(string path, List<StorageMove> moves)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        var move = moves.OrderByDescending(move => move.Source.Length).FirstOrDefault(move => Contains(move.Source, path));
        return move == null ? path : Path.Combine(move.Target, Path.GetRelativePath(move.Source, path));
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = mode, Pooling = false }.ToString());
    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{table}])";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (reader.GetString(1) == column) return true;
        return false;
    }
    private static void RejectLinkedAncestors(string path)
    {
        for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请直接选择实际目录，不能使用符号链接或目录联接。");
    }
    private static string Scratch(StorageLocations next, string id) => Path.Combine(next.TempPath, ".migration-" + id);
    private static bool Owns(string target, string id) => File.Exists(Path.Combine(target, Marker)) && File.ReadAllText(Path.Combine(target, Marker)) == id;
    private void RollbackCopies(List<StorageMove> moves, string id)
    {
        foreach (var move in moves.OrderByDescending(move => move.Target.Length))
        {
            if (!Owns(move.Target, id)) continue;
            RejectLinkedAncestors(move.Target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(move.Target))
            {
                if (store.IsBootstrapFile(entry)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            if (!Directory.EnumerateFileSystemEntries(move.Target).Any()) Directory.Delete(move.Target);
        }
    }
    private static void DeleteScratch(StorageLocations next, string id)
    {
        var scratch = Scratch(next, id);
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }
    private static void RemoveEmptyParents(string? path)
    {
        while (path != null && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }
}
