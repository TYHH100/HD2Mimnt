using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.IO;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 部署服务：DeploymentRecords/DeploymentFiles 表示当前安装状态，部署是对账而不是
/// “清理上一次会话再重建记录”。支持 Copy、SymbolicLink、HardLink。
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
    private readonly DeploymentStateMigrationService? _stateMigration;
    private readonly DeploymentPlanBuilder _planBuilder = new();
    private readonly DeploymentReconciler _reconciler;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    public event EventHandler? DeploymentsChanged;
    private void RaiseDeploymentsChanged() => DeploymentsChanged?.Invoke(this, EventArgs.Empty);

    public DeploymentService(
        IProfileRepository profileRepository,
        IModRepository modRepository,
        IDeploymentRepository deploymentRepository,
        IEnumerable<IDeploymentMethod> deploymentMethods,
        ILogger<DeploymentService>? logger = null,
        CrashDeploymentHistoryService? crashHistory = null,
        SettingsService? settingsService = null,
        DeploymentStateMigrationService? stateMigration = null)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _deploymentRepository = deploymentRepository;
        _logger = logger;
        _crashHistory = crashHistory;
        _settingsService = settingsService;
        _stateMigration = stateMigration;

        _deploymentMethods = new Dictionary<DeploymentMethod, IDeploymentMethod>();
        foreach (var method in deploymentMethods)
        {
            if (Enum.TryParse<DeploymentMethod>(method.Name, out var deploymentMethod))
            {
                _deploymentMethods[deploymentMethod] = method;
            }
        }
        _reconciler = new DeploymentReconciler(deploymentRepository, deploymentMethods);
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
    /// 获取最近的部署会话ID（旧接口兼容；新流程不再用会话驱动清理）。
    /// </summary>
    public async Task<Guid?> GetLatestDeploymentSessionIdAsync(CancellationToken cancellationToken = default)
    {
        return await _deploymentRepository.GetLatestSessionIdAsync(cancellationToken);
    }

    /// <summary>
    /// 获取当前安装的部署状态。没有游戏路径时回退旧接口，便于迁移前测试/工具读取。
    /// </summary>
    public async Task<IEnumerable<DeploymentRecord>> GetLatestDeploymentsAsync(
        CancellationToken cancellationToken = default)
    {
        var gamePath = EffectiveGamePath();
        if (gamePath != null)
        {
            var current = await _deploymentRepository.GetCurrentDeploymentsAsync(gamePath, cancellationToken);
            if (current.Count > 0) return current;
            // 未归属旧记录可继续用于升级前 UI/测试；不能把其它安装的当前状态混进来。
            return (await _deploymentRepository.GetLatestDeploymentsAsync(cancellationToken))
                .Where(record => string.IsNullOrWhiteSpace(record.GamePath));
        }
        return await _deploymentRepository.GetLatestDeploymentsAsync(cancellationToken);
    }

    /// <summary>获取指定游戏安装的当前部署状态。</summary>
    public async Task<IReadOnlyList<DeploymentRecord>> GetDeploymentsForGameAsync(string gamePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = DeploymentPlanBuilder.NormalizeGamePath(gamePath);
        return await _deploymentRepository.GetCurrentDeploymentsAsync(normalized, cancellationToken);
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
    /// 清理当前安装的部署文件和记录。没有游戏路径时回退清理最近会话，保持旧测试/工具兼容。
    /// </summary>
    public async Task<(int FilesDeleted, int RecordsDeleted)> PreDeploymentCleanupAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<DeploymentCleanupProgress>? fileProgress = null,
        IReadOnlyCollection<string>? preserveTargets = null)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report("开始 Pre-Deployment Cleanup...");
            var gamePath = EffectiveGamePath();
            await EnsureMigratedAsync(gamePath, cancellationToken);
            Guid? legacySessionId = null;
            List<DeploymentRecord> deploymentList;
            if (gamePath != null)
            {
                deploymentList = (await _deploymentRepository.GetCurrentDeploymentsAsync(gamePath, cancellationToken))
                    .ToList();
                if (deploymentList.Count == 0)
                {
                    // 未迁移的旧记录没有 GamePath；只回退清理这些旧记录，绝不清理其它安装的当前状态。
                    var latest = await GetLatestDeploymentSessionIdAsync(cancellationToken);
                    if (latest == null)
                    {
                        progress?.Report("没有找到当前安装的部署记录");
                        return (0, 0);
                    }
                    var legacy = (await GetDeploymentsBySessionIdAsync(latest.Value, cancellationToken))
                        .Where(record => string.IsNullOrWhiteSpace(record.GamePath)).ToList();
                    if (legacy.Count == 0)
                    {
                        progress?.Report("没有找到当前安装的部署记录");
                        return (0, 0);
                    }
                    deploymentList = legacy;
                    legacySessionId = latest;
                }
            }
            else
            {
                legacySessionId = await GetLatestDeploymentSessionIdAsync(cancellationToken);
                if (legacySessionId == null)
                {
                    progress?.Report("没有找到之前的部署记录");
                    return (0, 0);
                }
                progress?.Report($"找到最近部署会话: {legacySessionId}");
                deploymentList = (await GetDeploymentsBySessionIdAsync(legacySessionId.Value, cancellationToken)).ToList();
                if (deploymentList.Count == 0)
                {
                    await CleanupDeploymentRecordsAsync(legacySessionId.Value, cancellationToken);
                    return (0, 1);
                }
            }

            var totalFiles = deploymentList.Sum(record => record.Files.Count);
            var processedFiles = 0;
            var progressClock = System.Diagnostics.Stopwatch.StartNew();
            fileProgress?.Report(new(0, totalFiles));
            void FileProcessed()
            {
                processedFiles++;
                if (processedFiles == totalFiles || progressClock.ElapsedMilliseconds >= 100)
                {
                    fileProgress?.Report(new(processedFiles, totalFiles));
                    progressClock.Restart();
                }
            }

            var filesDeleted = 0;
            var recordsDeleted = 0;
            var preserved = 0;
            var allRecordsCleaned = true;
            foreach (var deployment in deploymentList)
            {
                var result = await CleanupDeploymentFilesAsync(deployment.Id, true, progress, cancellationToken,
                    FileProcessed, preserveTargets);
                filesDeleted += result.FilesDeleted;
                recordsDeleted += result.RecordDeleted ? 1 : 0;
                preserved += result.PreservedFiles;
                allRecordsCleaned &= result.RecordDeleted;
            }

            if (legacySessionId.HasValue && allRecordsCleaned)
            {
                // 旧记录已逐条删除；若会话里还有其它安装的记录不能按会话整删。
                await RecordRemovalAsync(legacySessionId.Value, null);
            }
            else if (gamePath != null && allRecordsCleaned)
            {
                foreach (var sessionId in deploymentList.Select(record => record.SessionId).Distinct())
                    await RecordRemovalAsync(sessionId, null);
            }

            var preservedSummary = preserved > 0 ? $"，保留未变更 {preserved} 个" : string.Empty;
            var location = legacySessionId.HasValue ? $"会话 {legacySessionId}" : $"安装 {gamePath}";
            progress?.Report($"Pre-Deployment Cleanup 完成: {location}，删除 {filesDeleted} 个文件{preservedSummary}, {recordsDeleted} 个记录");
            return (filesDeleted, recordsDeleted);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// 清理指定部署记录的所有文件
    /// </summary>
    public async Task<(int FilesDeleted, bool RecordDeleted)> CleanupDeploymentAsync(
        Guid deploymentId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var result = await CleanupDeploymentFilesAsync(deploymentId, true, progress, cancellationToken);
            return (result.FilesDeleted, result.RecordDeleted);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<(int FilesDeleted, bool RecordDeleted, int PreservedFiles)> CleanupDeploymentFilesAsync(
        Guid deploymentId, bool deleteRecord, IProgress<string>? progress, CancellationToken cancellationToken,
        Action? fileProcessed = null, IReadOnlyCollection<string>? preserveTargets = null)
    {
        try { return await CleanupDeploymentFilesCoreAsync(deploymentId, deleteRecord, progress, cancellationToken, fileProcessed, preserveTargets); }
        finally { RaiseDeploymentsChanged(); }
    }

    private async Task<(int FilesDeleted, bool RecordDeleted, int PreservedFiles)> CleanupDeploymentFilesCoreAsync(
        Guid deploymentId, bool deleteRecord, IProgress<string>? progress, CancellationToken cancellationToken,
        Action? fileProcessed, IReadOnlyCollection<string>? preserveTargets = null)
    {
        var deployment = await _deploymentRepository.GetByIdAsync(deploymentId, cancellationToken);
        if (deployment == null)
        {
            _logger?.LogWarning("部署记录 {DeploymentId} 不存在", deploymentId);
            return (0, false, 0);
        }

        var fileList = deployment.Files.ToList();
        var preserveSet = preserveTargets == null ? null
            : new HashSet<string>(preserveTargets.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        progress?.Report($"清理 Mod {deployment.ModId}: {fileList.Count} 个文件");

        var filesDeleted = 0;
        var filesPreserved = 0;
        var allFilesCleaned = true;
        foreach (var file in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (preserveSet != null && preserveSet.Contains(Path.GetFullPath(file.TargetPath)))
            {
                filesPreserved++;
                fileProcessed?.Invoke();
                continue;
            }

            // 链接部署中的空占位文件使用 Copy；旧记录未保存文件方法时才沿用会话方法。
            var methodName = string.IsNullOrWhiteSpace(file.Method) ? deployment.DeploymentMethod : file.Method;
            if (!Enum.TryParse<DeploymentMethod>(methodName, out var method)
                || !_deploymentMethods.TryGetValue(method, out var deploymentMethod))
            {
                allFilesCleaned = false;
                _logger?.LogError("文件部署方法无效或未注册: {Method}, {TargetPath}", methodName, file.TargetPath);
                fileProcessed?.Invoke();
                continue;
            }

            try
            {
                var success = await deploymentMethod.CleanupFileAsync(file.TargetPath, cancellationToken);
                if (success) filesDeleted++;
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
            fileProcessed?.Invoke();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (allFilesCleaned && deleteRecord)
        {
            // 只有所有目标文件都已清理，才删除记录；失败记录保留以便重试。
            await _deploymentRepository.DeleteDeploymentFilesByDeploymentIdAsync(deploymentId, cancellationToken);
            await _deploymentRepository.DeleteAsync(deploymentId, cancellationToken);
        }

        progress?.Report($"已清理 {filesDeleted}/{fileList.Count} 个文件");
        return (filesDeleted, allFilesCleaned, filesPreserved);
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
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await _deploymentRepository.GetByModIdAsync(modId, cancellationToken)).ToList();
            if (records.Count == 0) return;
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 编号调整与数据库提交前保留记录，失败时可以重试缺失文件的清理。
                var result = await CleanupDeploymentFilesAsync(record.Id, false, null, cancellationToken);
                if (!result.RecordDeleted)
                    throw new IOException($"模组部署文件未完全清理，已保留模组及部署记录，请重试（会话 {record.SessionId}）");
            }

            foreach (var group in records.GroupBy(record => record.GamePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(group.Key))
                    await CompactLegacyPatchIndexesAsync(group.ToList(), cancellationToken);
                else
                    await CompactPatchIndexesForGameAsync(group.Key, group.ToList(), cancellationToken);
            }

            RaiseDeploymentsChanged();
            foreach (var sessionId in records.Select(record => record.SessionId).Distinct())
                await RecordRemovalAsync(sessionId, new[] { modId });
            _logger?.LogInformation("已清理 Mod 部署文件及记录: {ModId}，{RecordsDeleted} 个记录", modId, records.Count);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task CompactPatchIndexesForGameAsync(string gamePath,
        IReadOnlyList<DeploymentRecord> removed, CancellationToken cancellationToken)
    {
        var removedIds = removed.Select(record => record.Id).ToHashSet();
        var current = await _deploymentRepository.GetCurrentDeploymentsAsync(gamePath, cancellationToken);
        var remaining = current.Where(record => !removedIds.Contains(record.Id))
            .SelectMany(record => record.Files).ToList();
        var renames = BuildCompactionRenames(remaining);
        cancellationToken.ThrowIfCancellationRequested();
        var moved = new List<(string Original, string Target)>();
        try
        {
            // 移动阶段不接受半途取消；按升序填补空位，不覆盖未追踪文件，链接移动不触碰源文件。
            foreach (var rename in renames)
            {
                // 目标已被外部删除时跳过物理移动但仍同步记录编号，缺失内容由下次部署对账按内容不匹配重写。
                if (!DeploymentTargetExists(rename.File.TargetPath))
                    continue;
                File.Move(rename.File.TargetPath, rename.TargetPath);
                moved.Add((rename.File.TargetPath, rename.TargetPath));
            }
            await _deploymentRepository.CommitModDeploymentRemovalAsync(removedIds,
                renames.Select(item => (item.File.Id, item.TargetPath)).ToList(), CancellationToken.None);
        }
        catch
        {
            foreach (var move in moved.AsEnumerable().Reverse())
                File.Move(move.Target, move.Original);
            throw;
        }
    }

    // 与 DeploymentReconciler 的存在判断一致：悬空符号链接仍算存在，交给物理移动抛出真实异常。
    private static bool DeploymentTargetExists(string path)
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

    private static List<(DeploymentFile File, string TargetPath)> BuildCompactionRenames(
        IReadOnlyList<DeploymentFile> remaining)
    {
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
        return renames;
    }

    /// <summary>旧会话记录的编号压缩，保持迁移前数据的删除能力。</summary>
    private async Task CompactLegacyPatchIndexesAsync(IReadOnlyList<DeploymentRecord> removed,
        CancellationToken cancellationToken)
    {
        var removedIds = removed.Select(record => record.Id).ToHashSet();
        var remaining = new List<DeploymentFile>();
        foreach (var sessionId in removed.Select(record => record.SessionId).Distinct())
            remaining.AddRange((await _deploymentRepository.GetBySessionIdAsync(sessionId, cancellationToken))
                .Where(record => !removedIds.Contains(record.Id)).SelectMany(record => record.Files));

        var renames = BuildCompactionRenames(remaining);
        cancellationToken.ThrowIfCancellationRequested();
        var moved = new List<(string Original, string Target)>();
        try
        {
            foreach (var rename in renames)
            {
                // 目标已被外部删除时跳过物理移动但仍同步记录编号，缺失内容由下次部署对账按内容不匹配重写。
                if (!DeploymentTargetExists(rename.File.TargetPath))
                    continue;
                File.Move(rename.File.TargetPath, rename.TargetPath);
                moved.Add((rename.File.TargetPath, rename.TargetPath));
            }
            await _deploymentRepository.CommitModDeploymentRemovalAsync(removedIds,
                renames.Select(item => (item.File.Id, item.TargetPath)).ToList(), CancellationToken.None);
        }
        catch
        {
            foreach (var move in moved.AsEnumerable().Reverse())
                File.Move(move.Target, move.Original);
            throw;
        }
    }

    private async Task RecordRemovalAsync(Guid sessionId, IReadOnlyCollection<Guid>? removedModIds)
    {
        if (_crashHistory == null) return;
        try { await _crashHistory.RecordRemovalAsync(sessionId, removedModIds, CancellationToken.None); }
        catch (Exception error) { _logger?.LogWarning(error, "记录部署清理时间线失败"); }
    }

    /// <summary>
    /// 部署整个 Profile：按配置对账当前安装状态，只改写有变化的文件。
    /// </summary>
    public async Task<DeploymentResult> DeployProfileAsync(
        Guid profileId,
        DeploymentMethod method,
        DeploymentOrder order,
        string gameDirectory,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (!_deploymentMethods.TryGetValue(method, out _))
                throw new InvalidOperationException($"未注册部署方式：{method}");
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                throw new DirectoryNotFoundException("游戏安装目录不存在");

            var gamePath = DeploymentPlanBuilder.NormalizeGamePath(gameDirectory);
            await EnsureMigratedAsync(gamePath, cancellationToken);
            var enabledIds = profile.Mods.Where(item => item.Enabled).Select(item => item.ModId).ToList();
            var modsById = (await _modRepository.GetByIdsAsync(enabledIds, cancellationToken))
                .ToDictionary(mod => mod.Id);
            foreach (var mod in modsById.Values)
                await BslProfilePolicy.EnsureDetectedAsync(mod, _modRepository, cancellationToken);

            var lockLoaders = _settingsService?.GetSetting(SettingKeys.ProfilesLockLoaders, true) ?? true;
            var trackedTargets = await LoadTrackedTargetsAsync(gamePath, cancellationToken);
            var plan = await _planBuilder.BuildProfilePlanAsync(profile, modsById, method, order, gamePath,
                lockLoaders, trackedTargets, cancellationToken);
            return await ApplyPlanAsync(plan, profile, progress, targeted: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CancelledResult(profileId, method, order);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署配置失败");
            return FailedResult(profileId, method, order, ex.Message);
        }
        finally
        {
            _applyGate.Release();
            RaiseDeploymentsChanged();
        }
    }

    /// <summary>
    /// 模组更新/切换版本后的自动增量部署。只重新规划被更新模组，其他模组沿用当前部署记录；
    /// 不应用配置页里尚未部署的启用/排序/选项改动。返回 null 表示该模组当前未参与此安装的部署。
    /// </summary>
    public async Task<DeploymentResult?> ReconcileAfterModUpdateAsync(Guid modId, string? gamePath = null,
        IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var effective = gamePath == null
                ? EffectiveGamePath()
                : DeploymentPlanBuilder.NormalizeGamePath(gamePath);
            if (string.IsNullOrWhiteSpace(effective) || !Directory.Exists(effective)) return null;
            await EnsureMigratedAsync(effective, cancellationToken);

            var current = await _deploymentRepository.GetCurrentDeploymentsAsync(effective, cancellationToken);
            var record = current.FirstOrDefault(item => item.ModId == modId);
            if (record == null) return null;
            var profile = await _profileRepository.GetByIdAsync(record.ProfileId, cancellationToken);
            if (profile == null) return null;
            var profileMod = profile.Mods.FirstOrDefault(item => item.ModId == modId);
            if (profileMod is not { Enabled: true }) return null;

            var modsById = (await _modRepository.GetByIdsAsync(current.Select(item => item.ModId).Distinct(),
                cancellationToken)).ToDictionary(mod => mod.Id);
            if (!modsById.ContainsKey(modId)) return null;

            var fallbackMethod = DeploymentPlanBuilder.ParseMethod(record.DeploymentMethod) ?? DeploymentMethod.Copy;
            var fallbackOrder = DeploymentPlanBuilder.ParseOrder(record.DeploymentOrder)
                ?? DeploymentPlanBuilder.ParseOrder(_settingsService?.GetSetting(
                    SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString()))
                ?? DeploymentOrder.TopToBottom;
            var plan = await _planBuilder.BuildTargetedPlanAsync(effective, modId, profile, current, modsById,
                fallbackMethod, fallbackOrder, cancellationToken);
            return await ApplyPlanAsync(plan, profile, progress, targeted: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new DeploymentResult
            {
                SessionId = Guid.NewGuid(), Status = DeploymentStatus.Cancelled,
                StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow,
                ErrorMessage = "自动部署已取消，可稍后重试部署"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "模组更新后的自动增量部署失败: {ModId}", modId);
            return new DeploymentResult
            {
                SessionId = Guid.NewGuid(), Status = DeploymentStatus.Failed,
                StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _applyGate.Release();
            RaiseDeploymentsChanged();
        }
    }

    private async Task<DeploymentResult> ApplyPlanAsync(DeploymentPlan plan, Profile profile,
        IProgress<DeploymentProgress>? progress, bool targeted, CancellationToken cancellationToken)
    {
        var result = new DeploymentResult
        {
            SessionId = plan.SessionId, ProfileId = plan.ProfileId, Method = plan.Method,
            Order = plan.Order, StartTime = DateTime.UtcNow, TotalMods = plan.TotalMods,
            SkippedMods = plan.SkippedModNames.Count
        };
        CrashDeploymentSnapshot? diagnosticSnapshot = null;
        try
        {
            await CheckPlanCompatibilityAsync(plan, cancellationToken);
            progress?.Report(new DeploymentProgress
            {
                Stage = "Preparing",
                Message = targeted ? "正在自动增量部署..." : "部署计划已生成，开始对账...",
                TotalMods = plan.TotalMods
            });

            if (_crashHistory != null)
            {
                try
                {
                    var selected = plan.Mods.Select(mod => (mod.Mod, mod.OptionsSnapshotJson ?? string.Empty,
                        (IReadOnlyList<string>)plan.Files.Where(file => file.ModId == mod.Mod.Id)
                            .Select(file => file.SourcePath).ToArray())).ToArray();
                    diagnosticSnapshot = await _crashHistory.CaptureAsync(plan.SessionId, profile, plan.GamePath,
                        DateTime.UtcNow, selected, cancellationToken);
                    await _crashHistory.SaveAsync(diagnosticSnapshot, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger?.LogWarning(error, "保存崩溃排查部署快照失败");
                }
            }

            var outcome = await _reconciler.ApplyAsync(plan, progress, cancellationToken);
            result.SessionId = outcome.SessionId;
            result.Status = outcome.Status;
            result.ErrorMessage = outcome.ErrorMessage;
            result.FilesDeleted = outcome.RemovedFiles;
            result.RemovedFiles = outcome.RemovedFiles;
            result.AddedFiles = outcome.AddedFiles;
            result.UpdatedFiles = outcome.UpdatedFiles;
            result.MovedFiles = outcome.MovedFiles;
            result.UnchangedFiles = outcome.UnchangedFiles;
            result.PreservedFiles = outcome.UnchangedFiles + outcome.MovedFiles;
            result.RenamedFiles = outcome.MovedFiles;
            result.CompletedMods = outcome.Status == DeploymentStatus.Completed ? plan.Mods.Count : 0;

            if (outcome.Status == DeploymentStatus.Completed)
            {
                result.Status = plan.SkippedModNames.Count > 0
                    ? DeploymentStatus.PartiallyFailed
                    : DeploymentStatus.Completed;
                result.Message = BuildSuccessMessage(plan, profile, outcome, targeted);
                progress?.Report(new DeploymentProgress
                {
                    Stage = "Completed", Message = result.Message, TotalMods = result.TotalMods,
                    CompletedMods = result.CompletedMods
                });
            }
            else if (outcome.Status == DeploymentStatus.Cancelled)
            {
                result.ErrorMessage = outcome.ErrorMessage ?? "部署已取消";
            }
            else
            {
                result.ErrorMessage = outcome.ErrorMessage ?? "部署失败";
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = DeploymentStatus.Cancelled;
            result.ErrorMessage = "部署已取消，已写入的文件可通过清理部署移除";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署对账失败");
            result.Status = DeploymentStatus.Failed;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.EndTime = DateTime.UtcNow;
            if (_crashHistory != null && diagnosticSnapshot != null)
            {
                try
                {
                    await _crashHistory.SaveAsync(
                        diagnosticSnapshot with { Status = result.Status.ToString(), EndedUtc = result.EndTime },
                        CancellationToken.None);
                }
                catch (Exception error) { _logger?.LogWarning(error, "完成崩溃排查部署快照失败"); }
            }
        }
        return result;
    }

    private static string BuildSuccessMessage(DeploymentPlan plan, Profile profile,
        ReconcileOutcome outcome, bool targeted)
    {
        var targetDirectory = plan.DataPath;
        if (outcome.AlreadyCurrent)
            return targeted
                ? $"该模组与当前部署一致，无需更新：{outcome.UnchangedFiles} 个文件保持原状。"
                : $"配置“{profile.Name}”与当前安装状态一致，无需重新部署：{outcome.UnchangedFiles} 个文件保持原状。位置：{targetDirectory}";

        var counts = $"新增 {outcome.AddedFiles}、更新 {outcome.UpdatedFiles}、移动 {outcome.MovedFiles}、删除 {outcome.RemovedFiles} 个文件";
        var message = targeted
            ? $"已自动增量部署：{counts}。"
            : $"配置“{profile.Name}”：已部署 {plan.Mods.Count}/{plan.TotalMods} 个模组，{counts}。位置：{targetDirectory}";
        if (plan.SkippedModNames.Count > 0)
            message += $"；无可部署补丁：{string.Join("、", plan.SkippedModNames)}";
        return message;
    }

    private static DeploymentResult CancelledResult(Guid profileId, DeploymentMethod method, DeploymentOrder order) => new()
    {
        SessionId = Guid.NewGuid(), ProfileId = profileId, Method = method, Order = order,
        Status = DeploymentStatus.Cancelled, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow,
        ErrorMessage = "部署已取消，已写入的文件可通过清理部署移除"
    };

    private static DeploymentResult FailedResult(Guid profileId, DeploymentMethod method, DeploymentOrder order,
        string errorMessage) => new()
    {
        SessionId = Guid.NewGuid(), ProfileId = profileId, Method = method, Order = order,
        Status = DeploymentStatus.Failed, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow,
        ErrorMessage = errorMessage
    };

    private async Task CheckPlanCompatibilityAsync(DeploymentPlan plan, CancellationToken cancellationToken)
    {
        // 自动增量里未变化的模组只沿用/改名现有目标，不读取源文件，也不应因旧源目录缺失阻断。
        var groups = plan.Files.Where(file => !file.TrustExisting)
            .Select(file => (
                SourceDirectory: Path.GetDirectoryName(file.SourcePath)!,
                TargetDirectory: Path.GetDirectoryName(file.TargetPath)!,
                file.Method))
            .Distinct()
            .ToList();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_deploymentMethods.TryGetValue(group.Method, out var deploymentMethod))
                throw new InvalidOperationException($"未注册部署方式：{group.Method}");
            var compatibility = await deploymentMethod.CheckCompatibilityAsync(
                group.SourceDirectory, group.TargetDirectory, cancellationToken);
            if (!compatibility.CanDeploy)
                throw new InvalidOperationException(
                    $"{group.TargetDirectory} 无法使用 {group.Method} 部署：{compatibility.ErrorMessage}");
        }
    }

    private async Task<HashSet<string>> LoadTrackedTargetsAsync(string gamePath, CancellationToken cancellationToken)
    {
        var records = await _deploymentRepository.GetCurrentDeploymentsAsync(gamePath, cancellationToken);
        return records.SelectMany(record => record.Files)
            .Select(file => DeploymentPlanBuilder.NormalizePath(file.TargetPath))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? EffectiveGamePath()
    {
        var path = _settingsService?.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return DeploymentPlanBuilder.NormalizeGamePath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task EnsureMigratedAsync(string? gamePath, CancellationToken cancellationToken)
    {
        if (_stateMigration == null) return;
        try { await _stateMigration.EnsureMigratedAsync(gamePath, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "部署状态懒迁移失败，继续使用现有记录");
        }
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
public sealed record DeploymentCleanupProgress(int ProcessedFiles, int TotalFiles);

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
    /// <summary>对账新增的目标文件数（含收养已有合法目标）。</summary>
    public int AddedFiles { get; set; }
    /// <summary>内容或链接身份变化而重写的目标文件数。</summary>
    public int UpdatedFiles { get; set; }
    /// <summary>仅目标编号位移、内容未变的文件数。</summary>
    public int MovedFiles { get; set; }
    /// <summary>删除的目标文件数。</summary>
    public int RemovedFiles { get; set; }
    /// <summary>内容校验通过、未改动的文件数。</summary>
    public int UnchangedFiles { get; set; }
    /// <summary>增量部署沿用的未变更目标文件数（含原地保留与改名就位）；完全一致时等于全部文件数。</summary>
    public int PreservedFiles { get; set; }
    /// <summary>增量部署中编号位移而移动就位的未变更文件数。</summary>
    public int RenamedFiles { get; set; }
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}
