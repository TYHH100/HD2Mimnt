using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Infrastructure.Hd2IAS.Companion;

/// <summary>
/// 把生成器产出的 armor 组件包按上游 external-runtime 语义落盘：
/// 差异计算（保留/覆盖/删除）→ 事务备份（N.original + HD2ExternalTransaction1）→
/// 原子写入与哈希回读 → 失败自动回滚 → 更新 deployment.json 的 armor 组件段。
/// 与上游共用同一把每游戏互斥体；除 armor 组件段外不改动 deployment.json 的任何字段。
/// </summary>
public static class IasCompanionApplier
{
    private const string ReceiptSchema = "HD2ExternalDeployment1";
    private const string DeploymentJson = "deployment.json";
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    private static readonly string[] PersonalRuntimePrefixes = ["presets/"];
    private static readonly string[] PersonalRuntimeFiles =
        ["physics.json", "optimization.json", "settings.ini", "settings.json"];

    /// <summary>armor 组件内保留用户数据的文件（与上游 PersonalRuntimeFile 语义一致）。</summary>
    public static bool IsPersonalRuntimeFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        return PersonalRuntimePrefixes.Any(prefix => normalized.StartsWith(prefix, IgnoreCase))
            || PersonalRuntimeFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<(int Written, int Deleted, int Preserved, string TransactionPath)> ApplyAsync(
        string gamePath, string runtimeRoot, string backupRoot, string packageDir,
        IReadOnlySet<string> retainedTargets, IReadOnlyList<string> obsoleteFiles,
        IProgress<string>? progress, CancellationToken token)
    {
        var fullGame = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var fullRuntime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        var armorRoot = Path.Combine(fullRuntime, "armor");
        EnsureGameNotRunning();
        using var mutex = AcquireGameMutex(fullGame);

        var receiptPath = Path.Combine(fullRuntime, DeploymentJson);
        if (!File.Exists(receiptPath))
            throw new InvalidOperationException("上游系统未部署（缺少 deployment.json），请先在上游完成系统安装。");
        var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(receiptPath, token))?.AsObject()
            ?? throw new InvalidDataException("上游部署记录无效。");
        ValidateReceipt(receipt, fullGame, fullRuntime);
        var armor = FindArmorComponent(receipt)
            ?? throw new InvalidOperationException("上游 armor 组件未部署，请先在上游完成系统安装。");

        var package = await ReadPackageAsync(packageDir, token);
        var oldFiles = ReadComponentFiles(armor);
        var newFiles = package.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        var changes = new List<IasTransactionChange>();
        var receiptRows = new List<JsonObject>();
        var receiptAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddReceiptRow(string path, JsonObject row)
        {
            if (!receiptAdded.Add(path)) return;
            receiptRows.Add(row);
        }
        var written = 0; var deleted = 0; var preserved = 0;
        var obsoleteSet = obsoleteFiles
            .Select(path => NormalizePath(path))
            .Where(path => path != null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 旧组件文件：保留（retained / 用户数据 / 内容被外部改动 / 在新包中）或删除。
        foreach (var (path, row) in oldFiles)
        {
            var rowPreserve = row["preserve_existing"]?.GetValue<bool>() == true;
            var rowSha = row["sha256"]?.GetValue<string>();
            var target = Path.Combine(armorRoot, path.Replace('/', '\\'));
            if (retainedTargets.Contains(path))
            {
                AddReceiptRow(path, row);
                obsoleteSet.Remove(target);
                continue;
            }
            if (newFiles.ContainsKey(path))
            {
                obsoleteSet.Remove(target);
                continue; // 由新包文件循环处理
            }
            if (rowPreserve || IsPersonalRuntimeFile(path))
            {
                preserved++;
                continue; // 文件保留，清单行移除（与上游 Preserved 语义一致）
            }
            var currentHash = FileHash(target);
            if (currentHash != null && currentHash != rowSha)
            {
                preserved++;
                continue; // 内容已被人改动：不删，仅移出清单
            }
            obsoleteSet.Remove(target);
            changes.Add(MakeChange(ComponentChangePath(path), target, currentHash, null, null));
            deleted++;
        }

        // 新包文件：retained 跳过；preserve/用户数据且已存在则沿用；否则覆盖写入。
        foreach (var row in package.Files)
        {
            if (retainedTargets.Contains(row.Path))
            {
                if (oldFiles.TryGetValue(row.Path, out var oldRow)) AddReceiptRow(row.Path, oldRow);
                else AddReceiptRow(row.Path, Row(row));
                continue;
            }
            var target = Path.Combine(armorRoot, row.Path.Replace('/', '\\'));
            var sourcePath = Path.Combine(packageDir, "files", row.Path.Replace('/', '\\'));
            if ((row.PreserveExisting || IsPersonalRuntimeFile(row.Path)) && File.Exists(target))
            {
                preserved++;
                AddReceiptRow(row.Path, Row(row));
                continue;
            }
            var currentHash = FileHash(target);
            changes.Add(MakeChange(ComponentChangePath(row.Path), target, currentHash, row.Sha256, sourcePath));
            written++;
            AddReceiptRow(row.Path, Row(row));
        }

        // 旧版直写部署的残留文件：不与新清单冲突时按事务删除。
        foreach (var obsolete in obsoleteSet)
        {
            if (!File.Exists(obsolete)) continue;
            var relative = ComponentChangePath(Path.GetRelativePath(armorRoot, obsolete));
            if (receiptRows.Any(node => node["path"]?.GetValue<string>()?.Equals(
                    relative["armor/".Length..], IgnoreCase) == true))
                continue; // 目标已被上游清单接管，交由上游管理
            if (relative.StartsWith("../", StringComparison.Ordinal))
                continue; // armor 目录外的历史残留不预测性删除，交由用户手动处理
            changes.Add(MakeChange(relative, obsolete, FileHash(obsolete), null, null));
            deleted++;
        }

        // 更新 receipt 的 armor 组件段并作为最后一个变更写入。
        var hasFileChanges = changes.Any(change => change.Before != change.After);
        if (hasFileChanges) receipt["updated_local"] = DateTimeOffset.Now.ToString("O");
        armor["files"] = new JsonArray(receiptRows.Select(row => (JsonNode)row.DeepClone()).ToArray());
        armor["version"] = package.Version;
        var newReceiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        var receiptChange = MakeChange(DeploymentJson, receiptPath, FileHash(receiptPath),
            Convert.ToHexStringLower(SHA256.HashData(newReceiptBytes)), null);        receiptChange.Content = newReceiptBytes;
        changes.Add(receiptChange);

        if (changes.All(change => change.Before == change.After))
            return (0, 0, preserved, string.Empty); // 全部一致，无需事务

        var transactionPath = await ExecuteTransactionAsync(fullGame, fullRuntime, backupRoot, changes,
            progress, token);
        return (written, deleted, preserved, transactionPath);
    }

    /// <summary>组件文件的变更路径为 runtime 根相对的 "armor/&lt;path&gt;"（与上游事务记录一致）。</summary>
    private static string ComponentChangePath(string componentPath)
        => "armor/" + componentPath.Replace('\\', '/').TrimStart('/');

    /// <summary>构造一个变更行；relativePath 为 runtime 根相对路径，targetPath 为其绝对路径。</summary>
    private static IasTransactionChange MakeChange(string relativePath, string targetPath,
        string? before, string? after, string? source)
    {
        var change = new IasTransactionChange
        {
            Path = relativePath.Replace('\\', '/'),
            Before = before,
            After = after,
            Source = source,
            PermanentDelete = after == null,
        };
        if (before != null && File.Exists(targetPath))
            change.BeforeTimeUtc = File.GetLastWriteTimeUtc(targetPath).Ticks;
        if (source != null && File.Exists(source))
            change.AfterTimeUtc = File.GetLastWriteTimeUtc(source).Ticks;
        return change;
    }

    // ---------- 事务执行 ----------

    private static async Task<string> ExecuteTransactionAsync(string fullGame, string fullRuntime,
        string backupRoot, List<IasTransactionChange> changes, IProgress<string>? progress, CancellationToken token)
    {
        var armorRoot = Path.Combine(fullRuntime, "armor");
        var receiptPath = Path.Combine(fullRuntime, DeploymentJson);
        var guardRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
        if (guardRoot.StartsWith(fullGame + Path.DirectorySeparatorChar, IgnoreCase)
            || guardRoot.StartsWith(fullRuntime + Path.DirectorySeparatorChar, IgnoreCase)
            || guardRoot.Equals(fullGame, IgnoreCase) || guardRoot.Equals(fullRuntime, IgnoreCase))
            throw new InvalidOperationException("备份目录须位于游戏和上游运行目录之外。");

        var backupDir = Path.Combine(backupRoot,
            $"external-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDir);
        var transactionPath = Path.Combine(backupDir, IasCompanionTransaction.TransactionFileName);

        var transaction = new IasCompanionTransaction
        {
            Game = fullGame,
            RuntimeRoot = fullRuntime,
            CreatedLocal = DateTimeOffset.Now.ToString("O"),
            Files = changes,
            Resolved = changes.ToDictionary(
                change => $"runtime:{change.Path}",
                change => Path.GetFullPath(Path.Combine(armorRoot, change.Path.Replace('/', '\\'))),
                StringComparer.OrdinalIgnoreCase),
        };

        // 备份将被覆盖或删除的现有文件。
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.Before == null) continue;
            var target = ResolvedPath(fullRuntime, change.Path);
            if (!File.Exists(target)) continue;
            var backupPath = Path.Combine(backupDir, $"{i}.original");
            await CopyAtomicAsync(backupPath, target, change.Before, token);
        }
        await SaveTransactionAsync(transactionPath, transaction, token);

        try
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                var target = ResolvedPath(fullRuntime, change.Path);
                if (change.Content != null)
                {
                    await WriteAtomicAsync(target, change.Content, change.After!, token);
                }
                else if (change.IsDelete)
                {
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                        File.Delete(target);
                    }
                }
                else if (change.Source != null)
                {
                    var bytes = await File.ReadAllBytesAsync(change.Source, token);
                    await WriteAtomicAsync(target, bytes, change.After!, token);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(change.Source));
                }
                progress?.Report(target);
            }

            // 全量回读校验。
            foreach (var change in changes)
            {
                var target = ResolvedPath(fullRuntime, change.Path);
                if (change.After == null)
                {
                    if (File.Exists(target)) throw new IOException($"删除校验失败：{change.Path}");
                }
                else if (FileHash(target) != change.After)
                {
                    throw new IOException($"外置组件写入校验失败：{change.Path}");
                }
            }
            transaction.Status = "installed";
            transaction.FinishedLocal = DateTimeOffset.Now.ToString("O");
            await SaveTransactionAsync(transactionPath, transaction, CancellationToken.None);
            return transactionPath;
        }
        catch
        {
            transaction.Status = "failed";
            try { await SaveTransactionAsync(transactionPath, transaction, CancellationToken.None); } catch { }
            try
            {
                await RestoreAsync(backupDir, fullRuntime, changes, CancellationToken.None);
                transaction.Status = "restored";
            }
            catch
            {
                transaction.Status = "recovery_required";
            }
            try { await SaveTransactionAsync(transactionPath, transaction, CancellationToken.None); } catch { }
            throw;
        }
    }

    private static async Task RestoreAsync(string backupDir, string fullRuntime,
        List<IasTransactionChange> changes, CancellationToken token)
    {
        for (var i = changes.Count - 1; i >= 0; i--)
        {
            var change = changes[i];
            var target = ResolvedPath(fullRuntime, change.Path);
            if (change.Before == null)
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    File.Delete(target);
                }
                continue;
            }
            var backupPath = Path.Combine(backupDir, $"{i}.original");
            if (!File.Exists(backupPath)) throw new IOException($"外置恢复备份缺失：{change.Path}");
            var bytes = await File.ReadAllBytesAsync(backupPath, token);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != change.Before)
                throw new IOException($"外置恢复备份已改变：{change.Path}");
            await WriteAtomicAsync(target, bytes, change.Before, token);
            if (change.BeforeTimeUtc > 0)
                File.SetLastWriteTimeUtc(target, new DateTime(change.BeforeTimeUtc, DateTimeKind.Utc));
        }
    }

    // ---------- 基础设施 ----------

    /// <summary>变更路径为 runtime 根相对（如 "armor/rig/x.rigbin"、"deployment.json"）。</summary>
    private static string ResolvedPath(string fullRuntime, string relativePath)
        => Path.GetFullPath(Path.Combine(fullRuntime, relativePath.Replace('/', '\\')));

    private static async Task WriteAtomicAsync(string target, byte[] bytes, string expectedSha256, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.hd2mimnt-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedSha256)
                throw new IOException($"写入内容校验失败：{target}");
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static async Task CopyAtomicAsync(string target, string source, string expectedSha256, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(source, token);
        await WriteAtomicAsync(target, bytes, expectedSha256, token);
    }

    private static async Task SaveTransactionAsync(string path, IasCompanionTransaction transaction, CancellationToken token)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(transaction), token);
    }

    private static string? FileHash(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void EnsureGameNotRunning()
    {
        if (Process.GetProcessesByName("helldivers2").Length > 0)
            throw new InvalidOperationException("请先正常退出游戏再更新 IAS 配套；程序不会强制结束游戏进程。");
    }

    /// <summary>与上游 Util.GameMutex 同名的每游戏互斥体，避免与上游并发写运行目录。</summary>
    private static Mutex AcquireGameMutex(string fullGame)
    {
        var name = "Local\\HD2OwnedFiles_"
            + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullGame.ToLowerInvariant())));
        var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                throw new InvalidOperationException("此游戏已有另一个管理器事务在运行，请稍后重试。");
            }
        }
        catch (AbandonedMutexException)
        {
            // 上游进程异常退出留下的互斥体：按上游语义继续。
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return mutex;
    }

    // ---------- receipt / package 读取 ----------

    internal static void ValidateReceipt(JsonObject receipt, string fullGame, string fullRuntime)
    {
        if (receipt["schema"]?.GetValue<string>() != ReceiptSchema)
            throw new InvalidDataException("上游部署记录身份不符（schema）。");
        if (!string.Equals(receipt["game"]?.GetValue<string>(), fullGame, IgnoreCase)
            || !string.Equals(receipt["runtime_root"]?.GetValue<string>(), fullRuntime, IgnoreCase))
            throw new InvalidDataException("上游部署记录身份不符（game/runtime_root）。");
        if (receipt["components"] is not JsonArray || ((JsonArray)receipt["components"]).Count > 3)
            throw new InvalidDataException("上游部署记录组件清单无效。");
    }

    public static JsonObject? FindArmorComponent(JsonObject receipt)
        => receipt["components"] is JsonArray components
            ? components.OfType<JsonObject>().FirstOrDefault(node => node["component"]?.GetValue<string>() == "armor")
            : null;

    private static Dictionary<string, JsonObject> ReadComponentFiles(JsonObject component)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (component["files"] is not JsonArray rows) return result;
        foreach (var node in rows.OfType<JsonObject>())
        {
            var path = node["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path) || node["sha256"] == null)
                throw new InvalidDataException($"上游部署记录文件行无效：{path}");
            result[path] = node;
        }
        return result;
    }

    private static JsonObject Row(IasRuntimeFile file)
    {
        var row = new JsonObject
        {
            ["path"] = file.Path,
            ["sha256"] = file.Sha256,
            ["bytes"] = file.Bytes,
        };
        if (file.PreserveExisting) row["preserve_existing"] = true;
        return row;
    }

    private static async Task<IasRuntimePackage> ReadPackageAsync(string packageDir, CancellationToken token)
    {
        var manifestPath = Path.Combine(packageDir, "external-runtime.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("生成器未产出组件清单。", manifestPath);
        var package = JsonSerializer.Deserialize<IasRuntimePackage>(await File.ReadAllBytesAsync(manifestPath, token))
            ?? throw new InvalidDataException("组件清单无效。");
        if (package.SchemaField != IasRuntimePackage.Schema || package.Component != "armor")
            throw new InvalidDataException("组件清单身份无效。");
        foreach (var file in package.Files)
        {
            var physical = Path.Combine(packageDir, "files", file.Path.Replace('/', '\\'));
            IasGeneratorAssets.VerifyFile(physical, file.Bytes, file.Sha256, file.Path);
        }
        return package;
    }
}
