using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

internal sealed record ReconcileOutcome(
    DeploymentStatus Status,
    Guid SessionId,
    int AddedFiles,
    int UpdatedFiles,
    int MovedFiles,
    int RemovedFiles,
    int UnchangedFiles,
    int RemovedMods,
    string? ErrorMessage,
    bool AlreadyCurrent);

/// <summary>
/// 对账执行器：先写临时文件/暂存移动，再删除不再需要的目标，最后一次性落位并提交当前状态。
/// 移动与落位阶段不响应取消；取消只发生在临时写入阶段，不会留下未登记的目标。
/// </summary>
internal sealed class DeploymentReconciler
{
    private enum OperationKind { Keep, Move, KeepAtNew, Replace, MoveReplace, Add, Adopt }

    private sealed record FileOperation(PlannedFile Desired, DeploymentFile? Current, OperationKind Kind);

    private readonly IDeploymentRepository _repository;
    private readonly IReadOnlyDictionary<DeploymentMethod, IDeploymentMethod> _methods;
    private readonly ILogger<DeploymentReconciler>? _logger;

    public DeploymentReconciler(IDeploymentRepository repository, IEnumerable<IDeploymentMethod> methods,
        ILogger<DeploymentReconciler>? logger = null)
    {
        _repository = repository;
        _logger = logger;
        var map = new Dictionary<DeploymentMethod, IDeploymentMethod>();
        foreach (var method in methods)
        {
            if (Enum.TryParse<DeploymentMethod>(method.Name, out var parsed) && Enum.IsDefined(parsed))
                map[parsed] = method;
        }
        _methods = map;
    }

    public async Task<ReconcileOutcome> ApplyAsync(DeploymentPlan plan,
        IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var newTemps = new List<(string Temp, string Target)>();
        var staged = new List<(string Temp, string Old, string New)>();
        try
        {
            var currentRecords = (await _repository.GetCurrentDeploymentsAsync(plan.GamePath, cancellationToken))
                .ToList();
            CleanupStaleTemps(plan);
            var currentFiles = currentRecords.SelectMany(record => record.Files).ToList();
            var byId = currentFiles.ToDictionary(file => file.Id);
            var byKey = new Dictionary<(Guid ModId, string Key), DeploymentFile>();
            var byTarget = new Dictionary<(Guid ModId, string Target), DeploymentFile>();
            foreach (var file in currentFiles)
            {
                if (!string.IsNullOrWhiteSpace(file.SourceKey))
                    byKey.TryAdd((file.ModId, file.SourceKey), file);
                var target = DeploymentPlanBuilder.NormalizePath(file.TargetPath);
                if (target != null) byTarget.TryAdd((file.ModId, target), file);
            }

            var consumed = new HashSet<Guid>();
            var operations = new List<FileOperation>();
            progress?.Report(new DeploymentProgress
            {
                Stage = "Comparing", Message = "检查未变更文件...", TotalMods = plan.TotalMods
            });

            foreach (var desired in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeploymentFile? current = null;
                if (desired.ExistingFileId is { } id) byId.TryGetValue(id, out current);
                if (current == null && !string.IsNullOrWhiteSpace(desired.SourceKey))
                    byKey.TryGetValue((desired.ModId, desired.SourceKey), out current);
                if (current == null && byTarget.TryGetValue((desired.ModId, desired.TargetPath), out var targetMatch))
                    current = targetMatch;
                if (current != null) consumed.Add(current.Id);

                var kind = await ClassifyAsync(desired, current, cancellationToken);
                operations.Add(new FileOperation(desired, current, kind));
            }

            var removed = currentFiles.Where(file => !consumed.Contains(file.Id)).ToList();
            var removedMods = currentRecords.Where(record => plan.Mods.All(mod => mod.Mod.Id != record.ModId)).ToList();
            var metadataChanged = RecordsNeedSave(plan, currentRecords);
            var fileChanged = operations.Any(operation => operation.Kind != OperationKind.Keep);
            if (!fileChanged && !metadataChanged && removed.Count == 0 && removedMods.Count == 0)
            {
                return new ReconcileOutcome(DeploymentStatus.Completed,
                    currentRecords.FirstOrDefault()?.SessionId ?? plan.SessionId,
                    0, 0, 0, 0, operations.Count, 0, null, true);
            }

            ValidateOperations(operations, removed);

            // 1. 写入临时文件；这是唯一响应取消的落盘阶段。
            foreach (var operation in operations.Where(operation =>
                         operation.Kind is OperationKind.Add or OperationKind.Replace or OperationKind.MoveReplace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(operation.Desired.TargetPath)!;
                Directory.CreateDirectory(directory);
                var temp = Path.Combine(directory, $".hd2mimnt_new_{Guid.NewGuid():N}");
                // 先登记临时名，方法在写入后抛出取消/失败时也能被清理。
                newTemps.Add((temp, operation.Desired.TargetPath));
                var method = GetMethod(operation.Desired.Method);
                progress?.Report(new DeploymentProgress
                {
                    Stage = "Writing", Message = $"准备写入：{Path.GetFileName(operation.Desired.TargetPath)}",
                    TotalMods = plan.TotalMods
                });
                var success = await method.DeployFileAsync(operation.Desired.SourcePath, temp, null, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (!success) throw new IOException($"写入临时文件失败：{operation.Desired.TargetPath}");
                if (operation.Desired.IsPatchBase
                    && (!File.Exists(temp) || new FileInfo(temp).Length == 0))
                    throw new InvalidDataException($"写入后的补丁主文件为空：{operation.Desired.TargetPath}");
            }

            // 2. 暂存移动。之后不再接受取消。
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var operation in operations.Where(operation => operation.Kind == OperationKind.Move))
            {
                if (!Exists(operation.Current!.TargetPath))
                    throw new IOException($"待移动的已部署文件缺失：{operation.Current.TargetPath}");
                var directory = Path.GetDirectoryName(operation.Desired.TargetPath)!;
                Directory.CreateDirectory(directory);
                var temp = Path.Combine(directory, $".hd2mimnt_stage_{Guid.NewGuid():N}");
                File.Move(operation.Current.TargetPath, temp);
                staged.Add((temp, operation.Current.TargetPath, operation.Desired.TargetPath));
            }

            // 3. 删除不再需要的目标；移动来源已经腾空。
            var removeTargets = removed.Select(file => file.TargetPath)
                .Concat(operations.Where(operation => operation.Kind == OperationKind.MoveReplace)
                    .Select(operation => operation.Current!.TargetPath))
                .Select(DeploymentPlanBuilder.NormalizePath)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var removedCount = 0;
            progress?.Report(new DeploymentProgress
            {
                Stage = "Removing", Message = "删除不再需要的部署文件...", TotalMods = plan.TotalMods
            });
            foreach (var target in removeTargets)
            {
                if (!Exists(target)) continue;
                if (Directory.Exists(target)) throw new IOException($"删除目标是目录：{target}");
                File.Delete(target);
                removedCount++;
            }

            // 4. 先登记当前状态，再落位。登记失败不会写出任何最终目标；
            //    落位失败时数据库已指向期望状态，下次对账按内容自愈。
            var matchedIds = operations.Where(operation => operation.Current != null)
                .ToDictionary(operation => (operation.Desired.ModId, operation.Desired.SourceKey),
                    operation => operation.Current!.Id);
            var desiredRecords = BuildRecords(plan, matchedIds);
            await _repository.SaveReconciledStateAsync(plan.GamePath, desiredRecords,
                removedMods.Select(record => record.Id).ToList(), CancellationToken.None);

            progress?.Report(new DeploymentProgress
            {
                Stage = "Committing", Message = "应用部署变更...", TotalMods = plan.TotalMods
            });
            foreach (var (temp, target) in newTemps)
            {
                File.Move(temp, target, overwrite: true);
            }
            newTemps.Clear();
            foreach (var (temp, _, target) in staged)
            {
                if (Exists(target)) throw new IOException($"移动目标已被占用：{target}");
                File.Move(temp, target);
            }
            staged.Clear();

            var added = operations.Count(operation => operation.Kind is OperationKind.Add or OperationKind.Adopt);
            var updated = operations.Count(operation => operation.Kind is OperationKind.Replace or OperationKind.MoveReplace);
            var moved = operations.Count(operation => operation.Kind is OperationKind.Move or OperationKind.KeepAtNew);
            var unchanged = operations.Count(operation => operation.Kind == OperationKind.Keep);
            return new ReconcileOutcome(DeploymentStatus.Completed, plan.SessionId, added, updated, moved,
                removedCount, unchanged, removedMods.Count, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupNewTemps(newTemps);
            RestoreStaged(staged);
            return new ReconcileOutcome(DeploymentStatus.Cancelled, plan.SessionId, 0, 0, 0, 0, 0, 0,
                "部署已取消，已写入的文件可通过清理部署移除", false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署对账失败: {GamePath}", plan.GamePath);
            CleanupNewTemps(newTemps);
            RestoreStaged(staged);
            return new ReconcileOutcome(DeploymentStatus.Failed, plan.SessionId, 0, 0, 0, 0, 0, 0,
                ex.Message, false);
        }
    }

    private async Task<OperationKind> ClassifyAsync(PlannedFile desired, DeploymentFile? current,
        CancellationToken cancellationToken)
    {
        if (current == null)
        {
            return await IsUpToDateAsync(desired, desired.TargetPath, cancellationToken)
                ? OperationKind.Adopt
                : OperationKind.Add;
        }

        var sameTarget = string.Equals(DeploymentPlanBuilder.NormalizePath(current.TargetPath),
            desired.TargetPath, StringComparison.OrdinalIgnoreCase);
        if (desired.TrustExisting)
        {
            if (sameTarget) return OperationKind.Keep;
            if (Exists(current.TargetPath)) return OperationKind.Move;
            if (await IsUpToDateAsync(desired, desired.TargetPath, cancellationToken))
                return OperationKind.KeepAtNew;
            throw new IOException($"当前部署文件缺失且无法恢复：{current.TargetPath}");
        }

        // 用户切换部署方式时不能只看内容相同就保留旧目标，否则目标仍是上一种文件形态。
        var methodMatches = string.Equals(current.Method, desired.Method.ToString(), StringComparison.Ordinal);
        if (sameTarget)
        {
            if (!methodMatches) return OperationKind.Replace;
            return await IsUpToDateAsync(desired, current.TargetPath, cancellationToken)
                ? OperationKind.Keep
                : OperationKind.Replace;
        }

        if (!methodMatches) return OperationKind.MoveReplace;
        return await IsUpToDateAsync(desired, current.TargetPath, cancellationToken)
            ? OperationKind.Move
            : OperationKind.MoveReplace;
    }

    private async Task<bool> IsUpToDateAsync(PlannedFile desired, string targetPath,
        CancellationToken cancellationToken)
    {
        var method = GetMethod(desired.Method);
        return await method.IsUpToDateAsync(desired.SourcePath, targetPath, cancellationToken);
    }

    private void ValidateOperations(IReadOnlyList<FileOperation> operations,
        IReadOnlyList<DeploymentFile> removed)
    {
        var removeTargets = removed.Select(file => DeploymentPlanBuilder.NormalizePath(file.TargetPath))
            .Concat(operations.Where(operation => operation.Kind == OperationKind.MoveReplace)
                .Select(operation => DeploymentPlanBuilder.NormalizePath(operation.Current!.TargetPath)))
            .Where(path => path != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stagedSources = operations.Where(operation => operation.Kind == OperationKind.Move)
            .Select(operation => DeploymentPlanBuilder.NormalizePath(operation.Current!.TargetPath))
            .Where(path => path != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            var target = operation.Desired.TargetPath;
            if (Directory.Exists(target))
                throw new IOException($"部署目标是目录：{target}");

            switch (operation.Kind)
            {
                case OperationKind.Add:
                case OperationKind.Replace:
                case OperationKind.MoveReplace:
                    var source = new FileInfo(operation.Desired.SourcePath);
                    if (!source.Exists) throw new FileNotFoundException("部署源文件缺失", operation.Desired.SourcePath);
                    if (operation.Desired.IsPatchBase && source.Length == 0)
                        throw new InvalidDataException($"补丁主文件为空：{operation.Desired.SourcePath}");
                    break;
                case OperationKind.Move:
                    if (!Exists(target)) break;
                    // 目标可以是另一个待移动来源（交换编号），也可以是本次将删除的旧目标；
                    // 其余已存在目标属于未追踪文件或仍被保留的目标，不能覆盖。
                    if (removeTargets.Contains(target) || stagedSources.Contains(target)) break;
                    throw new IOException($"移动目标已存在且不属于当前部署：{target}");
                case OperationKind.KeepAtNew:
                    if (!Exists(target))
                        throw new IOException($"已移动目标缺失：{target}");
                    break;
            }
        }
    }

    private List<DeploymentRecord> BuildRecords(DeploymentPlan plan,
        IReadOnlyDictionary<(Guid ModId, string SourceKey), Guid> matchedIds)
    {
        var now = DateTime.UtcNow;
        var result = new List<DeploymentRecord>();
        foreach (var mod in plan.Mods)
        {
            var record = new DeploymentRecord
            {
                Id = Guid.NewGuid(),
                SessionId = plan.SessionId,
                ProfileId = mod.ProfileId,
                ModId = mod.Mod.Id,
                Workflow = mod.Workflow,
                DeploymentMethod = mod.Method.ToString(),
                StartTime = now,
                EndTime = now,
                Status = DeploymentStatus.Completed.ToString(),
                OptionsSnapshotJson = mod.OptionsSnapshotJson,
                DeploymentOrderIndex = mod.OrderIndex,
                ModVersion = mod.ModVersion,
                GamePath = plan.GamePath,
                DeploymentOrder = plan.Order.ToString(),
                AppliedAt = now
            };
            record.Files = plan.Files.Where(file => file.ModId == mod.Mod.Id)
                .OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Select(file => new DeploymentFile
                {
                    Id = matchedIds.TryGetValue((file.ModId, file.SourceKey), out var id) ? id : Guid.NewGuid(),
                    ModId = file.ModId,
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath,
                    Method = file.Method.ToString(),
                    GamePath = plan.GamePath,
                    SourceKey = file.SourceKey,
                    SourceRelativePath = file.SourceRelativePath
                })
                .ToList();
            result.Add(record);
        }
        return result;
    }

    private bool RecordsNeedSave(DeploymentPlan plan, IReadOnlyList<DeploymentRecord> currentRecords)
    {
        if (currentRecords.Count != plan.Mods.Count) return true;
        var byMod = currentRecords.GroupBy(record => record.ModId).ToDictionary(group => group.Key, group => group.First());
        foreach (var mod in plan.Mods)
        {
            if (!byMod.TryGetValue(mod.Mod.Id, out var record)) return true;
            if (record.ProfileId != mod.ProfileId
                || !string.Equals(record.OptionsSnapshotJson, mod.OptionsSnapshotJson, StringComparison.Ordinal)
                || record.DeploymentOrderIndex != mod.OrderIndex
                || !string.Equals(record.ModVersion, mod.ModVersion, StringComparison.Ordinal)
                || !string.Equals(record.DeploymentMethod, mod.Method.ToString(), StringComparison.Ordinal)
                || !string.Equals(record.DeploymentOrder, plan.Order.ToString(), StringComparison.Ordinal)
                || !string.Equals(record.GamePath, plan.GamePath, StringComparison.OrdinalIgnoreCase))
                return true;

            var desiredFiles = plan.Files.Where(file => file.ModId == mod.Mod.Id).ToList();
            if (record.Files.Count != desiredFiles.Count) return true;
            foreach (var desired in desiredFiles)
            {
                var match = record.Files.FirstOrDefault(file =>
                                !string.IsNullOrWhiteSpace(file.SourceKey)
                                && string.Equals(file.SourceKey, desired.SourceKey, StringComparison.OrdinalIgnoreCase))
                            ?? record.Files.FirstOrDefault(file =>
                                string.Equals(DeploymentPlanBuilder.NormalizePath(file.TargetPath), desired.TargetPath,
                                    StringComparison.OrdinalIgnoreCase));
                if (match == null
                    || !string.Equals(DeploymentPlanBuilder.NormalizePath(match.TargetPath), desired.TargetPath,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(match.Method, desired.Method.ToString(), StringComparison.Ordinal)
                    || !string.Equals(DeploymentPlanBuilder.NormalizePath(match.SourcePath),
                        DeploymentPlanBuilder.NormalizePath(desired.SourcePath), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private IDeploymentMethod GetMethod(DeploymentMethod method)
        => _methods.TryGetValue(method, out var implementation)
            ? implementation
            : throw new InvalidOperationException($"未注册部署方式：{method}");

    private static bool Exists(string path)
    {
        try
        {
            if (File.Exists(path)) return true;
            return new FileInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CleanupStaleTemps(DeploymentPlan plan)
    {
        foreach (var directory in plan.Files.Select(file => Path.GetDirectoryName(file.TargetPath)!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var leftover in Directory.EnumerateFiles(directory, ".hd2mimnt_*"))
                    File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清扫失败不阻断部署；同名临时文件使用新 GUID。
            }
        }
    }

    private static void CleanupNewTemps(IEnumerable<(string Temp, string Target)> temps)
    {
        foreach (var (temp, _) in temps)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception) { /* 下次对账清扫 */ }
        }
    }

    private static void RestoreStaged(IEnumerable<(string Temp, string Old, string New)> staged)
    {
        foreach (var (temp, old, _) in staged.Reverse())
        {
            try
            {
                if (Exists(temp) && !Exists(old)) File.Move(temp, old);
            }
            catch (Exception) { /* 下次对账按目标内容恢复 */ }
        }
    }
}
