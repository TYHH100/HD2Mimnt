using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 部署服务
/// 统一的部署抽象层，支持三种部署方法：Copy, Symbolic Link, Hard Link
/// </summary>
public class DeploymentService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;
    private readonly IDeploymentRepository _deploymentRepository;
    private readonly ILogger<DeploymentService>? _logger;
    private readonly Dictionary<DeploymentMethod, IDeploymentMethod> _deploymentMethods;
    private readonly CrashDeploymentHistoryService? _crashHistory;
    private readonly SettingsService? _settingsService;
    public event EventHandler? DeploymentsChanged;
    private void RaiseDeploymentsChanged() => DeploymentsChanged?.Invoke(this, EventArgs.Empty);

    public DeploymentService(
        IProfileRepository profileRepository,
        IModRepository modRepository,
        IDeploymentRepository deploymentRepository,
        IEnumerable<IDeploymentMethod> deploymentMethods,
        ILogger<DeploymentService>? logger = null,
        CrashDeploymentHistoryService? crashHistory = null,
        SettingsService? settingsService = null)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _deploymentRepository = deploymentRepository;
        _logger = logger;
        _crashHistory = crashHistory;
        _settingsService = settingsService;

        // 注册所有部署方法
        _deploymentMethods = new Dictionary<DeploymentMethod, IDeploymentMethod>();
        foreach (var method in deploymentMethods)
        {
            if (Enum.TryParse<DeploymentMethod>(method.Name, out var deploymentMethod))
            {
                _deploymentMethods[deploymentMethod] = method;
            }
        }
    }

    /// <summary>
    /// 检查部署方法兼容性
    /// </summary>
    public async Task<(bool CanDeploy, string? ErrorMessage)> CheckDeploymentCompatibilityAsync(
        DeploymentMethod method,
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
        {
            return (false, $"Deployment method {method} is not registered");
        }

        return await deploymentMethod.CheckCompatibilityAsync(sourceDirectory, targetDirectory, cancellationToken);
    }

    /// <summary>
    /// 获取最近的部署会话ID
    /// </summary>
    public async Task<Guid?> GetLatestDeploymentSessionIdAsync(CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetLatestSessionIdAsync(cancellationToken);
    }

    /// <summary>
    /// 获取最近的部署记录
    /// </summary>
    public async Task<IEnumerable<DeploymentRecord>> GetLatestDeploymentsAsync(CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetLatestDeploymentsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取指定会话的部署记录
    /// </summary>
    public async Task<IEnumerable<DeploymentRecord>> GetDeploymentsBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetBySessionIdAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// 获取指定 Mod 的部署记录
    /// </summary>
    public async Task<IEnumerable<DeploymentRecord>> GetModDeploymentHistoryAsync(
        Guid modId,
        CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetByModIdAsync(modId, cancellationToken);
    }

    /// <summary>
    /// Pre-Deployment Cleanup
    /// 清理最近一次部署会话的所有文件和记录
    /// </summary>
    public async Task<(int FilesDeleted, int RecordsDeleted)> PreDeploymentCleanupAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("开始 Pre-Deployment Cleanup...");

        // 1. 获取最近的部署会话
        var latestSessionId = await GetLatestDeploymentSessionIdAsync(cancellationToken);
        if (latestSessionId == null)
        {
            progress?.Report("没有找到之前的部署记录");
            return (0, 0);
        }

        progress?.Report($"找到最近部署会话: {latestSessionId}");

        // 2. 获取该会话的所有部署记录
        var deployments = await GetDeploymentsBySessionIdAsync(latestSessionId.Value, cancellationToken);
        var deploymentList = deployments.ToList();

        if (deploymentList.Count == 0)
        {
            progress?.Report("部署会话中没有部署记录");
            await CleanupDeploymentRecordsAsync(latestSessionId.Value, cancellationToken);
            return (0, 1);
        }

        progress?.Report($"找到 {deploymentList.Count} 个部署记录");

        // 3. 遍历每个部署记录，清理其文件
        int totalFilesDeleted = 0;
        int totalRecordsDeleted = 0;
        var allRecordsCleaned = true;

        foreach (var deployment in deploymentList)
        {
            var (filesDeleted, recordDeleted) = await CleanupDeploymentAsync(
                deployment.Id,
                progress,
                cancellationToken);

            totalFilesDeleted += filesDeleted;
            totalRecordsDeleted += recordDeleted ? 1 : 0;
            allRecordsCleaned &= recordDeleted;
        }

        // 4. 仅在所有部署记录的文件均已清理时删除会话残留记录。
        if (allRecordsCleaned)
        {
            await CleanupDeploymentRecordsAsync(latestSessionId.Value, cancellationToken);
            if (_crashHistory != null)
            {
                try { await _crashHistory.RecordRemovalAsync(latestSessionId.Value, null, CancellationToken.None); }
                catch (Exception error) { _logger?.LogWarning(error, "记录部署清理时间线失败"); }
            }
        }

        progress?.Report($"Pre-Deployment Cleanup 完成: 会话 {latestSessionId.Value}，删除 {totalFilesDeleted} 个文件, {totalRecordsDeleted} 个记录");

        return (totalFilesDeleted, totalRecordsDeleted);
    }

    /// <summary>
    /// 清理指定部署记录的所有文件
    /// </summary>
    public async Task<(int FilesDeleted, bool RecordDeleted)> CleanupDeploymentAsync(
        Guid deploymentId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => await CleanupDeploymentFilesAsync(deploymentId, true, progress, cancellationToken);

    private async Task<(int FilesDeleted, bool RecordDeleted)> CleanupDeploymentFilesAsync(
        Guid deploymentId, bool deleteRecord, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try { return await CleanupDeploymentFilesCoreAsync(deploymentId, deleteRecord, progress, cancellationToken); }
        finally { RaiseDeploymentsChanged(); }
    }

    private async Task<(int FilesDeleted, bool RecordDeleted)> CleanupDeploymentFilesCoreAsync(
        Guid deploymentId, bool deleteRecord, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentRepository.GetByIdAsync(deploymentId, cancellationToken);
        if (deployment == null)
        {
            _logger?.LogWarning("部署记录 {DeploymentId} 不存在", deploymentId);
            return (0, false);
        }

        // 获取所有部署文件
        var files = await GetDeploymentFilesAsync(deploymentId, cancellationToken);
        var fileList = files.ToList();

        progress?.Report($"清理 Mod {deployment.ModId}: {fileList.Count} 个文件");

        int filesDeleted = 0;
        var allFilesCleaned = true;

        // 遍历清理每个文件
        foreach (var file in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 链接部署中的空占位文件使用 Copy；旧记录未保存文件方法时才沿用会话方法。
            var methodName = string.IsNullOrWhiteSpace(file.Method) ? deployment.DeploymentMethod : file.Method;
            if (!Enum.TryParse<DeploymentMethod>(methodName, out var method)
                || !_deploymentMethods.TryGetValue(method, out var deploymentMethod))
            {
                allFilesCleaned = false;
                _logger?.LogError("文件部署方法无效或未注册: {Method}, {TargetPath}", methodName, file.TargetPath);
                continue;
            }

            try
            {
                bool success = await deploymentMethod.CleanupFileAsync(file.TargetPath, cancellationToken);
                if (success)
                {
                    filesDeleted++;
                }
                else
                {
                    allFilesCleaned = false;
                    _logger?.LogWarning("清理文件失败: {TargetPath}", file.TargetPath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                allFilesCleaned = false;
                _logger?.LogError(ex, "清理文件异常: {TargetPath}", file.TargetPath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (allFilesCleaned && deleteRecord)
        {
            // 只有所有目标文件都已清理，才删除记录；失败记录保留以便重试。
            await _deploymentRepository.DeleteDeploymentFilesByDeploymentIdAsync(deploymentId, cancellationToken);
            await _deploymentRepository.DeleteAsync(deploymentId, cancellationToken);
        }

        progress?.Report($"已清理 {filesDeleted}/{fileList.Count} 个文件");

        return (filesDeleted, allFilesCleaned);
    }

    /// <summary>
    /// 清理部署记录（仅删除数据库记录）
    /// </summary>
    public async Task CleanupDeploymentRecordsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await _deploymentRepository.DeleteBySessionIdAsync(sessionId, cancellationToken);
        RaiseDeploymentsChanged();
        _logger?.LogInformation("已清理部署会话记录: {SessionId}", sessionId);
    }

    /// <summary>
    /// 清理指定 Mod 的已部署文件，成功后删除对应记录。
    /// 清理失败抛出异常，阻止 Delete Mod 生命周期继续删除模组和丢失重试记录。
    /// </summary>
    public async Task DeleteModDeploymentRecordsAsync(
        Guid modId,
        CancellationToken cancellationToken = default)
    {
        var records = (await _deploymentRepository.GetByModIdAsync(modId, cancellationToken)).ToList();
        if (records.Count == 0) return;
        var filesDeleted = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 编号调整与数据库提交前保留记录，失败时可以重试缺失文件的清理。
            var result = await CleanupDeploymentFilesAsync(record.Id, false, null, cancellationToken);
            if (!result.RecordDeleted)
                throw new IOException($"模组部署文件未完全清理，已保留模组及部署记录，请重试（会话 {record.SessionId}）");
            filesDeleted += result.FilesDeleted;
        }
        await CompactPatchIndexesAsync(records, cancellationToken);
        RaiseDeploymentsChanged();
        if (_crashHistory != null)
            foreach (var sessionId in records.Select(record => record.SessionId).Distinct())
            {
                try { await _crashHistory.RecordRemovalAsync(sessionId, new[] { modId }, CancellationToken.None); }
                catch (Exception error) { _logger?.LogWarning(error, "记录模组部署移除时间线失败"); }
            }
        _logger?.LogInformation("已清理 Mod 部署文件及记录: {ModId}，{FilesDeleted} 个文件，{RecordsDeleted} 个记录",
            modId, filesDeleted, records.Count);
    }

    private async Task CompactPatchIndexesAsync(IReadOnlyList<DeploymentRecord> removed, CancellationToken cancellationToken)
    {
        var removedIds = removed.Select(record => record.Id).ToHashSet();
        var remaining = new List<DeploymentFile>();
        foreach (var sessionId in removed.Select(record => record.SessionId).Distinct())
            remaining.AddRange((await _deploymentRepository.GetBySessionIdAsync(sessionId, cancellationToken))
                .Where(record => !removedIds.Contains(record.Id)).SelectMany(record => record.Files));

        // 当前目标编号已经体现实际部署方向及固定加载器排序，不能按配置页的显示顺序重排。
        var patches = remaining.Where(file => PatchDeploymentPlanner.TryParseTarget(file.TargetPath, out _, out _, out _))
            .Select(file =>
            {
                PatchDeploymentPlanner.TryParseTarget(file.TargetPath, out var prefix, out var index, out var suffix);
                return (File: file, Prefix: prefix, Index: index, Suffix: suffix);
            });
        var renames = new List<(DeploymentFile File, string TargetPath)>();
        foreach (var group in patches.GroupBy(item => Path.Combine(Path.GetDirectoryName(item.File.TargetPath)!, item.Prefix),
                     StringComparer.OrdinalIgnoreCase))
        {
            var next = 0;
            foreach (var triplet in group.GroupBy(item => item.Index).OrderBy(items => items.Key))
            {
                foreach (var item in triplet)
                {
                    var target = Path.Combine(Path.GetDirectoryName(item.File.TargetPath)!, $"{item.Prefix}.patch_{next}{item.Suffix}");
                    if (!target.Equals(item.File.TargetPath, StringComparison.OrdinalIgnoreCase))
                        renames.Add((item.File, target));
                }
                next++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var moved = new List<(string Original, string Target)>();
        try
        {
            // 移动阶段不接受半途取消；按升序填补空位，不覆盖未追踪文件，链接移动不触碰源文件。
            foreach (var rename in renames)
            {
                File.Move(rename.File.TargetPath, rename.TargetPath);
                moved.Add((rename.File.TargetPath, rename.TargetPath));
            }
            await _deploymentRepository.CommitModDeploymentRemovalAsync(removedIds,
                renames.Select(item => (item.File.Id, item.TargetPath)).ToList(), CancellationToken.None);
        }
        catch
        {
            // 未提交时恢复旧编号；删除记录仍保留，避免重试清理到其它模组改名后的文件。
            foreach (var move in moved.AsEnumerable().Reverse())
                File.Move(move.Target, move.Original);
            throw;
        }
    }

    /// <summary>
    /// 部署整个 Profile
    /// 按照 Profile 中 Mod 的顺序进行部署
    /// </summary>
    public async Task<DeploymentResult> DeployProfileAsync(
        Guid profileId,
        DeploymentMethod method,
        DeploymentOrder order,
        string gameDirectory,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DeploymentResult
        {
            SessionId = Guid.NewGuid(), ProfileId = profileId, Method = method,
            Order = order, StartTime = DateTime.UtcNow
        };
        CrashDeploymentSnapshot? diagnosticSnapshot = null;
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
                throw new InvalidOperationException($"未注册部署方式：{method}");
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                throw new DirectoryNotFoundException("游戏安装目录不存在");
            var targetDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "data");
            var modsById = (await _modRepository.GetByIdsAsync(profile.Mods.Where(item => item.Enabled)
                .Select(item => item.ModId), cancellationToken)).ToDictionary(mod => mod.Id);
            foreach (var mod in modsById.Values)
                await BslProfilePolicy.EnsureDetectedAsync(mod, _modRepository, cancellationToken);
            var orderedMods = BslProfilePolicy.DeploymentSequence(profile, modsById, order,
                _settingsService?.GetSetting(SettingKeys.ProfilesLockLoaders, true) ?? true);
            result.TotalMods = orderedMods.Count;
            var plans = new List<(Mod Mod, List<PatchDeploymentPlanner.FileEntry> Files)>();
            var nextIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skippedNames = new List<string>();
            var nativeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trackedNativeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var previous in await GetLatestDeploymentsAsync(cancellationToken))
                foreach (var file in await GetDeploymentFilesAsync(previous.Id, cancellationToken))
                    trackedNativeTargets.Add(Path.GetFullPath(file.TargetPath));

            // 在清理旧部署前按配置快照展开选项并检查方法，错误设置不能破坏现有部署。
            foreach (var profileMod in orderedMods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                modsById.TryGetValue(profileMod.ModId, out var mod);
                var files = mod != null && Directory.Exists(mod.Path)
                    ? await Task.Run(() => PatchDeploymentPlanner.CollectSelectedFiles(mod, profileMod.OptionsJson, cancellationToken), cancellationToken)
                    : new List<string>();
                var nativeFiles = mod?.WorkflowType == Hd2IrIntegrationService.Workflow
                    ? await Hd2IrIntegrationService.GetNativeFilesAsync(mod, profileMod.OptionsJson, cancellationToken) : [];
                foreach (var file in nativeFiles)
                {
                    var target = Path.GetFullPath(Path.Combine(gameDirectory, file.FileName));
                    if (!nativeTargets.Add(target)) throw new InvalidOperationException($"HD2IR 组件目标重复：{file.FileName}，请在配置选项中只启用一份。");
                    if (Path.Exists(target) && !trackedNativeTargets.Contains(target))
                        throw new IOException($"HD2IR 目标已存在且不属于当前部署：{file.FileName}，请先备份并处理原有文件。");
                }
                if (mod == null || files.Count + nativeFiles.Count == 0)
                {
                    result.SkippedMods++;
                    skippedNames.Add(mod?.Name ?? profileMod.ModId.ToString());
                    continue;
                }
                progress?.Report(new DeploymentProgress
                {
                    Stage = "Preparing", Message = $"检查部署兼容性：{mod.Name}", TotalMods = result.TotalMods
                });
                foreach (var sourceDirectory in files.Concat(nativeFiles.Select(file => file.SourcePath!))
                    .Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var compatibility = await deploymentMethod.CheckCompatibilityAsync(sourceDirectory!, targetDirectory, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!compatibility.CanDeploy)
                        throw new InvalidOperationException($"{mod.Name} 无法使用 {method} 部署：{compatibility.ErrorMessage}");
                }
                var plannedFiles = await Task.Run(() =>
                {
                    var planned = PatchDeploymentPlanner.BuildFiles(files, nextIndexes, cancellationToken);
                    foreach (var file in planned)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!PatchDeploymentPlanner.TryParseTarget(file.FileName, out _, out _, out var suffix) || suffix.Length != 0)
                            continue;
                        var source = new FileInfo(file.SourcePath!);
                        if (!source.Exists || source.Length == 0)
                            throw new InvalidDataException($"补丁主文件不存在或为空：{file.SourcePath}");
                    }
                    return planned;
                }, cancellationToken);
                plannedFiles.AddRange(nativeFiles);
                plans.Add((mod, plannedFiles));
            }

            var enabledLoaders = orderedMods.Count(item => modsById.TryGetValue(item.ModId, out var mod)
                && BslProfilePolicy.IsLoader(BslProfilePolicy.Role(mod)));
            if (enabledLoaders > 1)
                throw new InvalidOperationException("BSL 与 MDL 互斥，同一配置只能启用一个加载器，请停用另一份或重复副本");
            var loaderCount = plans.Count(plan => BslProfilePolicy.IsLoader(BslProfilePolicy.Role(plan.Mod)));
            if (loaderCount > 1)
                throw new InvalidOperationException("同一配置只能部署一个 BSL / MDL 加载器，请停用重复副本");
            if (loaderCount == 0 && plans.Any(plan => BslProfilePolicy.Role(plan.Mod) is BslModRole.Dependency or BslModRole.SmoothBoot))
                throw new InvalidOperationException("此配置包含依赖加载器模组或 SmoothBoot，请先添加并启用 Bingus Shared Loader 或 MDL 及其补丁选项");

            if (plans.Count == 0 && result.SkippedMods > 0)
                throw new InvalidOperationException($"当前配置没有可部署的补丁，跳过 {result.SkippedMods} 个模组：{string.Join("、", skippedNames)}");

            progress?.Report(new DeploymentProgress { Stage = "Cleaning", Message = "清理之前的部署...", TotalMods = result.TotalMods });
            var previousSession = await GetLatestDeploymentSessionIdAsync(cancellationToken);
            var (filesDeleted, _) = await PreDeploymentCleanupAsync(
                new InlineProgress<string>(message => progress?.Report(new DeploymentProgress
                {
                    Stage = "Cleaning", Message = message, TotalMods = result.TotalMods
                })), cancellationToken);
            result.FilesDeleted = filesDeleted;
            if (previousSession.HasValue && (await GetDeploymentsBySessionIdAsync(previousSession.Value, cancellationToken)).Any())
                throw new IOException("之前的部署未完全清理，请重试清理后再部署");

            result.Status = DeploymentStatus.Deploying;
            if (_crashHistory != null)
            {
                try
                {
                    diagnosticSnapshot = await _crashHistory.CaptureAsync(result.SessionId, profile, gameDirectory, DateTime.UtcNow,
                        plans.Select(plan => (plan.Mod, orderedMods.First(item => item.ModId == plan.Mod.Id).OptionsJson,
                            (IReadOnlyList<string>)plan.Files.Where(file => file.SourcePath != null).Select(file => file.SourcePath!).ToArray())).ToArray(), cancellationToken);
                    await _crashHistory.SaveAsync(diagnosticSnapshot, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { _logger?.LogWarning(error, "保存崩溃排查部署快照失败"); }
            }
            Directory.CreateDirectory(targetDirectory);
            var deploymentOrderIndex = 0;
            foreach (var (mod, files) in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = ProfileModOptionResolver.ResolveEffective(mod.Manifest,
                    orderedMods.First(item => item.ModId == mod.Id).OptionsJson).ToJson();
                var record = await CreateDeploymentRecordAsync(result.SessionId, profileId, mod.Id, method,
                    cancellationToken: cancellationToken, optionsSnapshotJson: options,
                    deploymentOrderIndex: deploymentOrderIndex++, modVersion: mod.Version);
                try
                {
                    // 一次事务预登记整个模组，避免每复制一个文件都提交 SQLite。
                    // 占位文件仍独立记录为 Copy，取消/失败后也能清理全部目标。
                    await _deploymentRepository.AddDeploymentFilesAsync(files.Select(file => new DeploymentFile
                    {
                        Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = mod.Id,
                        SourcePath = file.SourcePath ?? string.Empty,
                        TargetPath = Path.GetFullPath(Path.Combine(file.RelativeToGameRoot ? gameDirectory : targetDirectory, file.FileName)),
                        Method = (file.SourcePath == null ? DeploymentMethod.Copy : method).ToString()
                    }).ToList(), cancellationToken);
                    var hasFailure = false;
                    var done = 0;
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = Path.GetFullPath(Path.Combine(file.RelativeToGameRoot ? gameDirectory : targetDirectory, file.FileName));
                        progress?.Report(new DeploymentProgress
                        {
                            Stage = "Deploying", Message = $"正在部署 {mod.Name}：{done}/{files.Count}，{file.FileName}",
                            CurrentMod = mod.Name, TotalMods = result.TotalMods, CompletedMods = result.CompletedMods,
                            CurrentModFiles = done, CurrentModTotalFiles = files.Count
                        });
                        if (file.SourcePath == null)
                        {
                            // 先解除可能存在的链接，不能截断链接指向的源文件。
                            File.Delete(target);
                            using var placeholder = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                        }
                        else
                        {
                            var success = await deploymentMethod.DeployFileAsync(file.SourcePath, target, null, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (success && !file.RelativeToGameRoot
                                && PatchDeploymentPlanner.TryParseTarget(file.FileName, out _, out _, out var suffix) && suffix.Length == 0
                                && new FileInfo(target).Length == 0)
                                throw new InvalidDataException($"部署后的补丁主文件为空：{target}");
                            hasFailure |= !success;
                        }
                        done++;
                    }
                    await UpdateDeploymentStatusAsync(record.Id,
                        hasFailure ? DeploymentStatus.PartiallyFailed : DeploymentStatus.Completed, DateTime.UtcNow, CancellationToken.None);
                    if (hasFailure) result.FailedMods++;
                    else result.CompletedMods++;
                }
                catch (OperationCanceledException)
                {
                    await UpdateDeploymentStatusAsync(record.Id, DeploymentStatus.Cancelled, DateTime.UtcNow, CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "部署模组 {ModName} 失败", mod.Name);
                    result.FailedMods++;
                    result.ErrorMessage = $"{mod.Name}：{ex.Message}";
                    await UpdateDeploymentStatusAsync(record.Id, DeploymentStatus.Failed, DateTime.UtcNow, CancellationToken.None);
                }
            }

            result.Status = result.FailedMods > 0 || result.SkippedMods > 0 ? DeploymentStatus.PartiallyFailed : DeploymentStatus.Completed;
            result.Message = $"配置“{profile.Name}”：已部署 {result.CompletedMods}/{result.TotalMods} 个模组，失败 {result.FailedMods} 个，跳过 {result.SkippedMods} 个。位置：{targetDirectory}";
            if (skippedNames.Count > 0) result.Message += $"；无可部署补丁：{string.Join("、", skippedNames)}";
            progress?.Report(new DeploymentProgress
            {
                Stage = "Completed", Message = result.Message, TotalMods = result.TotalMods, CompletedMods = result.CompletedMods
            });
        }
        catch (OperationCanceledException)
        {
            result.Status = DeploymentStatus.Cancelled;
            result.ErrorMessage = "部署已取消，已写入的文件可通过清理部署移除";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署配置失败");
            result.Status = DeploymentStatus.Failed;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.EndTime = DateTime.UtcNow;
            if (_crashHistory != null && diagnosticSnapshot != null)
            {
                try { await _crashHistory.SaveAsync(diagnosticSnapshot with { Status = result.Status.ToString(), EndedUtc = result.EndTime }, CancellationToken.None); }
                catch (Exception error) { _logger?.LogWarning(error, "完成崩溃排查部署快照失败"); }
            }
            RaiseDeploymentsChanged();
        }
        return result;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    /// <summary>
    /// 创建部署记录
    /// </summary>
    public async Task<DeploymentRecord> CreateDeploymentRecordAsync(
        Guid sessionId,
        Guid profileId,
        Guid modId,
        DeploymentMethod method,
        string? workflow = null,
        CancellationToken cancellationToken = default,
        string? optionsSnapshotJson = null,
        int? deploymentOrderIndex = null,
        string? modVersion = null)
    {
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ProfileId = profileId,
            ModId = modId,
            Workflow = workflow ?? string.Empty,
            DeploymentMethod = method.ToString(),
            StartTime = DateTime.UtcNow,
            Status = DeploymentStatus.Pending.ToString(),
            OptionsSnapshotJson = optionsSnapshotJson,
            DeploymentOrderIndex = deploymentOrderIndex,
            ModVersion = modVersion
        };

        await _deploymentRepository.AddAsync(record, cancellationToken);
        RaiseDeploymentsChanged();
        return record;
    }

    /// <summary>
    /// 更新部署记录状态
    /// </summary>
    public async Task UpdateDeploymentStatusAsync(
        Guid recordId,
        DeploymentStatus status,
        DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        var record = await _deploymentRepository.GetByIdAsync(recordId, cancellationToken);
        if (record == null)
        {
            throw new InvalidOperationException($"Deployment record {recordId} not found");
        }

        record.Status = status.ToString();
        if (endTime.HasValue)
        {
            record.EndTime = endTime.Value;
        }

        await _deploymentRepository.UpdateAsync(record, cancellationToken);
        RaiseDeploymentsChanged();
    }

    /// <summary>
    /// 添加部署文件记录
    /// </summary>
    public async Task AddDeploymentFileAsync(
        Guid deploymentId,
        Guid modId,
        string sourcePath,
        string targetPath,
        DeploymentMethod method,
        CancellationToken cancellationToken = default)
    {
        var file = new DeploymentFile
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            ModId = modId,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Method = method.ToString()
        };

        await _deploymentRepository.AddDeploymentFileAsync(file, cancellationToken);
        RaiseDeploymentsChanged();
    }

    /// <summary>
    /// 批量添加部署文件记录
    /// </summary>
    public async Task AddDeploymentFilesAsync(
        Guid deploymentId,
        Guid modId,
        IEnumerable<(string SourcePath, string TargetPath)> files,
        DeploymentMethod method,
        CancellationToken cancellationToken = default)
    {
        var deploymentFiles = files.Select(f => new DeploymentFile
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            ModId = modId,
            SourcePath = f.SourcePath,
            TargetPath = f.TargetPath,
            Method = method.ToString()
        }).ToList();

        await _deploymentRepository.AddDeploymentFilesAsync(deploymentFiles, cancellationToken);
        RaiseDeploymentsChanged();
    }

    /// <summary>
    /// 获取部署文件列表
    /// </summary>
    public async Task<IEnumerable<DeploymentFile>> GetDeploymentFilesAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetDeploymentFilesAsync(deploymentId, cancellationToken);
    }

    /// <summary>
    /// 检查目标文件是否已被部署
    /// </summary>
    public async Task<DeploymentFile?> GetDeploymentFileByTargetPathAsync(
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetDeploymentFileByTargetPathAsync(targetPath, cancellationToken);
    }

    /// <summary>
    /// 部署单个文件
    /// </summary>
    public async Task<bool> DeployFileAsync(
        DeploymentMethod method,
        string sourcePath,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
        {
            _logger?.LogError("部署方法 {Method} 未注册", method);
            return false;
        }

        try
        {
            return await deploymentMethod.DeployFileAsync(sourcePath, targetPath, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署文件失败: {SourcePath} -> {TargetPath}", sourcePath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// 清理已部署的文件
    /// </summary>
    public async Task<bool> CleanupDeployedFileAsync(
        DeploymentMethod method,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
        {
            _logger?.LogError("部署方法 {Method} 未注册", method);
            return false;
        }

        try
        {
            return await deploymentMethod.CleanupFileAsync(targetPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理文件失败: {TargetPath}", targetPath);
            return false;
        }
    }

    /// <summary>
    /// 获取支持的部署方法列表
    /// </summary>
    public IEnumerable<DeploymentMethod> GetSupportedDeploymentMethods()
    {
        return _deploymentMethods.Keys;
    }
}

/// <summary>
/// 部署进度
/// </summary>
public class DeploymentProgress
{
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? CurrentMod { get; set; }
    public int TotalMods { get; set; }
    public int CompletedMods { get; set; }
    public int CurrentModFiles { get; set; }
    public int CurrentModTotalFiles { get; set; }
}

/// <summary>
/// 部署结果
/// </summary>
public class DeploymentResult
{
    public Guid SessionId { get; set; }
    public Guid ProfileId { get; set; }
    public DeploymentMethod Method { get; set; }
    public DeploymentOrder Order { get; set; }
    public DeploymentStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int TotalMods { get; set; }
    public int CompletedMods { get; set; }
    public int FailedMods { get; set; }
    public int SkippedMods { get; set; }
    public int FilesDeleted { get; set; }
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}
