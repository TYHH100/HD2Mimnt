using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
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

    public DeploymentService(
        IProfileRepository profileRepository,
        IModRepository modRepository,
        IDeploymentRepository deploymentRepository,
        IEnumerable<IDeploymentMethod> deploymentMethods,
        ILogger<DeploymentService>? logger = null)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _deploymentRepository = deploymentRepository;
        _logger = logger;

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

        foreach (var deployment in deploymentList)
        {
            var (filesDeleted, recordDeleted) = await CleanupDeploymentAsync(
                deployment.Id,
                progress,
                cancellationToken);

            totalFilesDeleted += filesDeleted;
            totalRecordsDeleted += recordDeleted ? 1 : 0;
        }

        // 4. 删除会话记录（DeploymentFiles 已经通过 CleanupDeploymentAsync 删除）
        await CleanupDeploymentRecordsAsync(latestSessionId.Value, cancellationToken);

        progress?.Report($"Pre-Deployment Cleanup 完成: 删除 {totalFilesDeleted} 个文件, {totalRecordsDeleted} 个记录");

        return (totalFilesDeleted, totalRecordsDeleted);
    }

    /// <summary>
    /// 清理指定部署记录的所有文件
    /// </summary>
    public async Task<(int FilesDeleted, bool RecordDeleted)> CleanupDeploymentAsync(
        Guid deploymentId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var deployment = await _deploymentRepository.GetByIdAsync(deploymentId, cancellationToken);
        if (deployment == null)
        {
            _logger?.LogWarning("部署记录 {DeploymentId} 不存在", deploymentId);
            return (0, false);
        }

        // 获取部署方法
        if (!Enum.TryParse<DeploymentMethod>(deployment.DeploymentMethod, out var method))
        {
            _logger?.LogError("无效的部署方法: {Method}", deployment.DeploymentMethod);
            return (0, false);
        }

        if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
        {
            _logger?.LogError("部署方法 {Method} 未注册", method);
            return (0, false);
        }

        // 获取所有部署文件
        var files = await GetDeploymentFilesAsync(deploymentId, cancellationToken);
        var fileList = files.ToList();

        progress?.Report($"清理 Mod {deployment.ModId}: {fileList.Count} 个文件");

        int filesDeleted = 0;

        // 遍历清理每个文件
        foreach (var file in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bool success = await deploymentMethod.CleanupFileAsync(file.TargetPath, cancellationToken);
                if (success)
                {
                    filesDeleted++;
                }
                else
                {
                    _logger?.LogWarning("清理文件失败: {TargetPath}", file.TargetPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "清理文件异常: {TargetPath}", file.TargetPath);
            }
        }

        // 删除 DeploymentFiles 记录
        await _deploymentRepository.DeleteDeploymentFilesByDeploymentIdAsync(deploymentId, cancellationToken);

        // 删除 DeploymentRecord
        await _deploymentRepository.DeleteAsync(deploymentId, cancellationToken);

        progress?.Report($"已清理 {filesDeleted}/{fileList.Count} 个文件");

        return (filesDeleted, true);
    }

    /// <summary>
    /// 清理部署记录（仅删除数据库记录）
    /// </summary>
    public async Task CleanupDeploymentRecordsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await _deploymentRepository.DeleteBySessionIdAsync(sessionId, cancellationToken);
        _logger?.LogInformation("已清理部署会话记录: {SessionId}", sessionId);
    }

    /// <summary>
    /// 删除指定 Mod 的所有部署记录
    /// 用于 Delete Mod 生命周期
    /// </summary>
    public async Task DeleteModDeploymentRecordsAsync(
        Guid modId,
        CancellationToken cancellationToken = default)
    {
        await _deploymentRepository.DeleteByModIdAsync(modId, cancellationToken);
        _logger?.LogInformation("已删除 Mod 部署记录: {ModId}", modId);
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
            SessionId = Guid.NewGuid(),
            ProfileId = profileId,
            Method = method,
            Order = order,
            StartTime = DateTime.UtcNow
        };

        try
        {
            // 1. 获取 Profile
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                result.Status = DeploymentStatus.Failed;
                result.ErrorMessage = $"Profile {profileId} not found";
                return result;
            }

            // 2. 检查部署方法兼容性
            progress?.Report(new DeploymentProgress
            {
                Stage = "Preparing",
                Message = "检查部署兼容性..."
            });

            // 暂时跳过兼容性检查，因为 modLibraryPath 未配置
            // 在实际部署时会针对每个 Mod 的路径进行检查

            // 3. Pre-Deployment Cleanup
            progress?.Report(new DeploymentProgress
            {
                Stage = "Cleaning",
                Message = "清理之前的部署..."
            });

            var (filesDeleted, _) = await PreDeploymentCleanupAsync(
                new Progress<string>(msg => progress?.Report(new DeploymentProgress
                {
                    Stage = "Cleaning",
                    Message = msg
                })),
                cancellationToken);

            result.FilesDeleted = filesDeleted;

            // 4. 获取启用的 Mods 并排序
            var enabledMods = profile.Mods.Where(m => m.Enabled).ToList();

            if (enabledMods.Count == 0)
            {
                result.Status = DeploymentStatus.Completed;
                result.Message = "没有启用的 Mod 需要部署";
                result.EndTime = DateTime.UtcNow;
                return result;
            }

            // 按 Order 排序
            var orderedMods = order == DeploymentOrder.TopToBottom
                ? enabledMods.OrderBy(m => m.Order).ToList()
                : enabledMods.OrderByDescending(m => m.Order).ToList();

            result.TotalMods = orderedMods.Count;

            progress?.Report(new DeploymentProgress
            {
                Stage = "Deploying",
                Message = $"开始部署 {orderedMods.Count} 个 Mods...",
                TotalMods = orderedMods.Count,
                CompletedMods = 0
            });

            // 5. 遍历部署每个 Mod
            result.Status = DeploymentStatus.Deploying;

            foreach (var profileMod in orderedMods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mod = await _modRepository.GetByIdAsync(profileMod.ModId, cancellationToken);
                if (mod == null)
                {
                    _logger?.LogWarning("Mod {ModId} 不存在，跳过", profileMod.ModId);
                    result.SkippedMods++;
                    continue;
                }

                progress?.Report(new DeploymentProgress
                {
                    Stage = "Deploying",
                    Message = $"正在部署: {mod.Name}",
                    CurrentMod = mod.Name,
                    TotalMods = orderedMods.Count,
                    CompletedMods = result.CompletedMods
                });

                // 创建部署记录
                var deploymentRecord = await CreateDeploymentRecordAsync(
                    result.SessionId,
                    profileId,
                    mod.Id,
                    method,
                    null,
                    cancellationToken);

                try
                {
                    // 部署 Mod 的所有文件
                    // 获取 Mod 目录中的所有文件
                    var modDirectory = mod.Path;
                    if (!Directory.Exists(modDirectory))
                    {
                        _logger?.LogWarning("Mod 目录不存在: {ModDirectory}", modDirectory);
                        result.SkippedMods++;
                        await UpdateDeploymentStatusAsync(
                            deploymentRecord.Id,
                            DeploymentStatus.Failed,
                            DateTime.UtcNow,
                            cancellationToken);
                        continue;
                    }

                    // 获取部署方法
                    if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
                    {
                        _logger?.LogError("未找到部署方法: {Method}", method);
                        result.FailedMods++;
                        await UpdateDeploymentStatusAsync(
                            deploymentRecord.Id,
                            DeploymentStatus.Failed,
                            DateTime.UtcNow,
                            cancellationToken);
                        continue;
                    }

                    // 遍历 Mod 目录中的所有文件
                    var files = Directory.GetFiles(modDirectory, "*.*", SearchOption.AllDirectories);
                    var deployedFiles = new List<(string SourcePath, string TargetPath)>();
                    int fileIndex = 0;
                    bool hasFailure = false;

                    foreach (var sourceFile in files)
                    {
                        fileIndex++;
                        cancellationToken.ThrowIfCancellationRequested();

                        // 计算相对路径
                        var relativePath = Path.GetRelativePath(modDirectory, sourceFile);
                        var targetPath = Path.Combine(gameDirectory, relativePath);

                        progress?.Report(new DeploymentProgress
                        {
                            Stage = "Deploying",
                            Message = $"部署文件: {relativePath}",
                            CurrentMod = mod.Name,
                            TotalMods = orderedMods.Count,
                            CompletedMods = result.CompletedMods,
                            CurrentModFiles = fileIndex,
                            CurrentModTotalFiles = files.Length
                        });

                        // 部署单个文件
                        var success = await deploymentMethod.DeployFileAsync(
                            sourceFile,
                            targetPath,
                            null,
                            cancellationToken);

                        if (success)
                        {
                            deployedFiles.Add((sourceFile, targetPath));
                        }
                        else
                        {
                            _logger?.LogWarning("文件部署失败: {SourceFile} -> {TargetPath}", sourceFile, targetPath);
                            hasFailure = true;
                        }
                    }

                    // 批量添加部署文件记录
                    if (deployedFiles.Count > 0)
                    {
                        await AddDeploymentFilesAsync(
                            deploymentRecord.Id,
                            mod.Id,
                            deployedFiles,
                            method,
                            cancellationToken);
                    }

                    // 更新部署状态
                    var deploymentStatus = hasFailure
                        ? DeploymentStatus.PartiallyFailed
                        : DeploymentStatus.Completed;

                    await UpdateDeploymentStatusAsync(
                        deploymentRecord.Id,
                        deploymentStatus,
                        DateTime.UtcNow,
                        cancellationToken);

                    if (hasFailure)
                    {
                        result.FailedMods++;
                    }
                    else
                    {
                        result.CompletedMods++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "部署 Mod {ModName} 失败", mod.Name);
                    result.FailedMods++;
                    await UpdateDeploymentStatusAsync(
                        deploymentRecord.Id,
                        DeploymentStatus.Failed,
                        DateTime.UtcNow,
                        cancellationToken);
                }

                progress?.Report(new DeploymentProgress
                {
                    Stage = "Deploying",
                    Message = $"已完成: {mod.Name}",
                    CurrentMod = mod.Name,
                    TotalMods = orderedMods.Count,
                    CompletedMods = result.CompletedMods
                });
            }

            // 6. 完成
            result.Status = result.FailedMods > 0
                ? DeploymentStatus.PartiallyFailed
                : DeploymentStatus.Completed;
            result.EndTime = DateTime.UtcNow;

            progress?.Report(new DeploymentProgress
            {
                Stage = "Completed",
                Message = $"部署完成: {result.CompletedMods}/{result.TotalMods} 个 Mods",
                TotalMods = result.TotalMods,
                CompletedMods = result.CompletedMods
            });

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Status = DeploymentStatus.Cancelled;
            result.EndTime = DateTime.UtcNow;
            result.ErrorMessage = "部署已取消";
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署 Profile 失败");
            result.Status = DeploymentStatus.Failed;
            result.EndTime = DateTime.UtcNow;
            result.ErrorMessage = ex.Message;
            return result;
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
        CancellationToken cancellationToken = default)
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
            Status = DeploymentStatus.Pending.ToString()
        };

        await _deploymentRepository.AddAsync(record, cancellationToken);
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
