using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 统一部署服务（增强版）
/// 集成 LogicalMod 和传统 Mod 的部署能力
/// </summary>
public class UnifiedDeploymentService
{
    private readonly IDeploymentRepository _deploymentRepository;
    private readonly IFileOwnershipRepository _fileOwnershipRepository;
    private readonly IDeploymentTransactionRepository _transactionRepository;
    private readonly ILogger<UnifiedDeploymentService>? _logger;
    private readonly DiffEngine _diffEngine;
    private readonly ArchiveExtractionService _extractionService;
    private readonly Dictionary<DeploymentMethod, IDeploymentMethod> _deploymentMethods;

    public UnifiedDeploymentService(
        IDeploymentRepository deploymentRepository,
        IFileOwnershipRepository fileOwnershipRepository,
        IDeploymentTransactionRepository transactionRepository,
        DiffEngine diffEngine,
        ArchiveExtractionService extractionService,
        IEnumerable<IDeploymentMethod> deploymentMethods,
        ILogger<UnifiedDeploymentService>? logger = null)
    {
        _deploymentRepository = deploymentRepository;
        _fileOwnershipRepository = fileOwnershipRepository;
        _transactionRepository = transactionRepository;
        _diffEngine = diffEngine;
        _extractionService = extractionService;
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
    /// 部署单个模组源（支持 Mod 和 LogicalMod）
    /// </summary>
    public async Task<UnifiedDeploymentResult> DeployModSourceAsync(
        IDeployableModSource modSource,
        DeploymentMethod method,
        string gameDirectory,
        Guid sessionId,
        Guid? profileId = null,
        IProgress<UnifiedDeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new UnifiedDeploymentResult
        {
            SessionId = sessionId,
            ModSourceId = modSource.Id,
            ModSourceName = modSource.Name,
            ModType = modSource.ModType,
            Method = method,
            StartTime = DateTime.UtcNow
        };

        try
        {
            _logger?.LogInformation("开始部署 {ModType}: {Name}", modSource.ModType, modSource.Name);

            progress?.Report(new UnifiedDeploymentProgress
            {
                Stage = "Preparing",
                Message = $"准备部署: {modSource.Name}",
                ModName = modSource.Name
            });

            // 1. 获取所有可部署文件
            var desiredFiles = await modSource.GetDeployableFilesAsync(cancellationToken);
            var desiredFileList = desiredFiles.ToList();

            result.TotalFiles = desiredFileList.Count;

            if (desiredFileList.Count == 0)
            {
                result.Status = DeploymentStatus.Completed;
                result.Message = "没有文件需要部署";
                result.EndTime = DateTime.UtcNow;
                return result;
            }

            _logger?.LogInformation("发现 {Count} 个可部署文件", desiredFileList.Count);

            // 2. 计算 Diff
            progress?.Report(new UnifiedDeploymentProgress
            {
                Stage = "Planning",
                Message = "计算部署差异...",
                ModName = modSource.Name
            });

            var diff = await _diffEngine.CalculateDiffAsync(desiredFileList, sessionId, cancellationToken);

            result.FilesToAdd = diff.FilesToAdd.Count;
            result.FilesToUpdate = diff.FilesToUpdate.Count;
            result.FilesToRemove = diff.FilesToRemove.Count;
            result.FilesUnchanged = diff.FilesUnchanged.Count;

            _logger?.LogInformation(
                "Diff: Add={Add}, Update={Update}, Remove={Remove}, Unchanged={Unchanged}",
                diff.FilesToAdd.Count, diff.FilesToUpdate.Count, diff.FilesToRemove.Count, diff.FilesUnchanged.Count);

            // 3. 检查是否为空事务
            if (_diffEngine.IsEmptyDiff(diff))
            {
                _logger?.LogInformation("内容一致，无需部署");
                result.Status = DeploymentStatus.Completed;
                result.Message = "内容一致，无需写入；未创建事务";
                result.EndTime = DateTime.UtcNow;
                return result;
            }

            // 4. 创建事务记录
            var transaction = new DeploymentTransaction
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ProfileId = profileId,
                LogicalModId = modSource.ModType == "LogicalMod" ? modSource.Id : null,
                ModId = modSource.ModType == "Traditional" ? modSource.Id : null,
                Operation = "Deploy",
                Method = method,
                Status = DeploymentStatus.Deploying,
                StartTime = DateTime.UtcNow
            };

            await _transactionRepository.AddAsync(transaction, cancellationToken);
            _logger?.LogInformation("创建事务: {TransactionId}", transaction.Id);

            // 5. 获取部署方法
            if (!_deploymentMethods.TryGetValue(method, out var deploymentMethod))
            {
                throw new InvalidOperationException($"部署方法 {method} 未注册");
            }

            // 5. 执行部署
            progress?.Report(new UnifiedDeploymentProgress
            {
                Stage = "Deploying",
                Message = "开始部署文件...",
                ModName = modSource.Name,
                TotalFiles = diff.TotalChanges,
                CompletedFiles = 0
            });

            result.Status = DeploymentStatus.Deploying;

            // 5.1 处理需要添加的文件
            foreach (var file in diff.FilesToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await DeployFileAsync(file, deploymentMethod, gameDirectory, sessionId, modSource, profileId, cancellationToken);
                result.FilesDeployed++;
                transaction.AddedFiles.Add(file.TargetPath);

                progress?.Report(new UnifiedDeploymentProgress
                {
                    Stage = "Deploying",
                    Message = $"添加: {Path.GetFileName(file.TargetPath)}",
                    ModName = modSource.Name,
                    TotalFiles = diff.TotalChanges,
                    CompletedFiles = result.FilesDeployed
                });
            }

            // 5.2 处理需要更新的文件
            foreach (var file in diff.FilesToUpdate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await DeployFileAsync(file, deploymentMethod, gameDirectory, sessionId, modSource, profileId, cancellationToken);
                result.FilesDeployed++;
                transaction.UpdatedFiles.Add(file.TargetPath);

                progress?.Report(new UnifiedDeploymentProgress
                {
                    Stage = "Deploying",
                    Message = $"更新: {Path.GetFileName(file.TargetPath)}",
                    ModName = modSource.Name,
                    TotalFiles = diff.TotalChanges,
                    CompletedFiles = result.FilesDeployed
                });
            }

            // 5.3 处理需要删除的文件
            foreach (var file in diff.FilesToRemove)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await CleanupFileAsync(file, deploymentMethod, cancellationToken);
                result.FilesDeleted++;
                transaction.RemovedFiles.Add(file.TargetPath);

                progress?.Report(new UnifiedDeploymentProgress
                {
                    Stage = "Cleaning",
                    Message = $"删除: {Path.GetFileName(file.TargetPath)}",
                    ModName = modSource.Name,
                    TotalFiles = diff.TotalChanges,
                    CompletedFiles = result.FilesDeployed + result.FilesDeleted
                });
            }

            // 5.4 记录跳过的文件
            foreach (var file in diff.FilesUnchanged)
            {
                transaction.SkippedFiles.Add(file.TargetPath);
            }

            // 6. 更新事务状态为完成
            transaction.Status = DeploymentStatus.Completed;
            transaction.EndTime = DateTime.UtcNow;
            await _transactionRepository.UpdateStatusAsync(transaction.Id, DeploymentStatus.Completed, transaction.EndTime, null, cancellationToken);

            result.Status = DeploymentStatus.Completed;
            result.Message = "部署完成";
            result.EndTime = DateTime.UtcNow;

            _logger?.LogInformation(
                "部署完成: {Name}, 耗时={Duration}ms, 部署={Deployed}, 删除={Deleted}, 跳过={Skipped}",
                modSource.Name,
                (result.EndTime - result.StartTime).TotalMilliseconds,
                result.FilesDeployed,
                result.FilesDeleted,
                result.FilesUnchanged);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "部署失败: {Name}", modSource.Name);
            result.Status = DeploymentStatus.Failed;
            result.ErrorMessage = ex.Message;
            result.EndTime = DateTime.UtcNow;

            // 更新事务状态为失败
            try
            {
                var transaction = await _transactionRepository.GetBySessionIdAsync(sessionId, cancellationToken);
                if (transaction != null)
                {
                    await _transactionRepository.UpdateStatusAsync(
                        transaction.Id,
                        DeploymentStatus.Failed,
                        DateTime.UtcNow,
                        ex.Message,
                        cancellationToken);
                }
            }
            catch (Exception txEx)
            {
                _logger?.LogError(txEx, "更新事务状态失败");
            }

            return result;
        }
    }

    /// <summary>
    /// 部署单个文件
    /// </summary>
    private async Task DeployFileAsync(
        DeployableFileMapping file,
        IDeploymentMethod deploymentMethod,
        string gameDirectory,
        Guid sessionId,
        IDeployableModSource modSource,
        Guid? profileId,
        CancellationToken cancellationToken)
    {
        string sourceFilePath = file.SourcePath;
        string? tempFilePath = null;

        try
        {
            // 如果需要从归档提取
            if (file.RequiresExtraction && !string.IsNullOrEmpty(file.ArchiveInternalPath))
            {
                tempFilePath = await _extractionService.ExtractFileAsync(
                    file.SourcePath,
                    file.ArchiveInternalPath,
                    cancellationToken);
                sourceFilePath = tempFilePath;
            }

            var targetPath = Path.Combine(gameDirectory, file.TargetPath);

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 执行部署
            bool success = await deploymentMethod.DeployFileAsync(sourceFilePath, targetPath, null, cancellationToken);

            if (success)
            {
                // 计算哈希（如果尚未计算）
                var contentHash = file.Hash;
                if (string.IsNullOrEmpty(contentHash))
                {
                    contentHash = await _extractionService.CalculateFastFingerprintAsync(targetPath, cancellationToken);
                }

                // 记录文件所有权
                var ownership = new FileOwnership
                {
                    Id = Guid.NewGuid(),
                    LogicalModId = modSource.Id,
                    ComponentId = file.ComponentId ?? Guid.Empty,
                    PackageId = file.PackageId ?? Guid.Empty,
                    DeployPath = file.TargetPath,
                    ContentHash = contentHash,
                    TransactionId = Guid.Parse(sessionId.ToString()),
                    DeployedAt = DateTime.UtcNow,
                    IsShared = file.IsShared,
                    ReferenceCount = 1
                };

                await _fileOwnershipRepository.AddAsync(ownership, cancellationToken);

                _logger?.LogDebug("已部署: {TargetPath}", file.TargetPath);
            }
            else
            {
                _logger?.LogWarning("部署失败: {TargetPath}", file.TargetPath);
            }
        }
        finally
        {
            // 清理临时文件
            if (!string.IsNullOrEmpty(tempFilePath))
            {
                _extractionService.CleanupTempFile(tempFilePath);
            }
        }
    }

    /// <summary>
    /// 清理单个文件
    /// </summary>
    private async Task CleanupFileAsync(
        DeployableFileMapping file,
        IDeploymentMethod deploymentMethod,
        CancellationToken cancellationToken)
    {
        try
        {
            // 检查是否为共享资源
            var ownership = await _fileOwnershipRepository.GetByDeployPathAsync(file.TargetPath, cancellationToken);
            if (ownership != null)
            {
                if (ownership.IsShared && ownership.ReferenceCount > 1)
                {
                    // 减少引用计数
                    await _fileOwnershipRepository.UpdateReferenceCountAsync(file.TargetPath, -1, cancellationToken);
                    _logger?.LogDebug("共享文件引用计数减少: {Path}, RefCount={Count}",
                        file.TargetPath, ownership.ReferenceCount - 1);
                    return;
                }

                // 删除文件
                bool success = await deploymentMethod.CleanupFileAsync(file.TargetPath, cancellationToken);
                if (success)
                {
                    // 删除所有权记录
                    await _fileOwnershipRepository.DeleteByDeployPathAsync(file.TargetPath, cancellationToken);
                    _logger?.LogDebug("已清理: {TargetPath}", file.TargetPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理文件失败: {TargetPath}", file.TargetPath);
        }
    }
}

/// <summary>
/// 统一部署结果
/// </summary>
public class UnifiedDeploymentResult
{
    public Guid SessionId { get; set; }
    public Guid ModSourceId { get; set; }
    public string ModSourceName { get; set; } = string.Empty;
    public string ModType { get; set; } = string.Empty;
    public DeploymentMethod Method { get; set; }
    public DeploymentStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TotalFiles { get; set; }
    public int FilesToAdd { get; set; }
    public int FilesToUpdate { get; set; }
    public int FilesToRemove { get; set; }
    public int FilesUnchanged { get; set; }
    public int FilesDeployed { get; set; }
    public int FilesDeleted { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 统一部署进度
/// </summary>
public class UnifiedDeploymentProgress
{
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
}
