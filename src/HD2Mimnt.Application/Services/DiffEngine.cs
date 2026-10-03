using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Diff Engine - 计算部署差异
/// 对比 Desired State 和 Current State，生成最小变更集
/// </summary>
public class DiffEngine
{
    private readonly IDeploymentRepository _deploymentRepository;
    private readonly IFileOwnershipRepository _fileOwnershipRepository;
    private readonly ILogger<DiffEngine>? _logger;

    public DiffEngine(
        IDeploymentRepository deploymentRepository,
        IFileOwnershipRepository fileOwnershipRepository,
        ILogger<DiffEngine>? logger = null)
    {
        _deploymentRepository = deploymentRepository;
        _fileOwnershipRepository = fileOwnershipRepository;
        _logger = logger;
    }

    /// <summary>
    /// 计算部署差异
    /// </summary>
    /// <param name="desiredFiles">期望的文件状态（来自 LogicalMod 或 Mod）</param>
    /// <param name="sessionId">当前会话ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>部署差异结果</returns>
    public async Task<DeploymentDiff> CalculateDiffAsync(
        IEnumerable<DeployableFileMapping> desiredFiles,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var diff = new DeploymentDiff
        {
            SessionId = sessionId,
            CalculatedAt = DateTime.UtcNow
        };

        var desiredFileList = desiredFiles.ToList();
        _logger?.LogInformation("开始计算 Diff: Desired={Count} 个文件", desiredFileList.Count);

        // 获取当前已部署的文件（基于 FileOwnership）
        var currentOwnerships = await _fileOwnershipRepository.GetAllAsync(cancellationToken);
        var currentOwnershipDict = currentOwnerships.ToDictionary(o => o.DeployPath, o => o);

        _logger?.LogInformation("当前已部署: {Count} 个文件", currentOwnershipDict.Count);

        // 构建期望文件字典
        var desiredFileDict = desiredFileList.ToDictionary(f => f.TargetPath, f => f);

        // 1. 查找需要添加的文件（Desired 中有，Current 中没有）
        foreach (var desired in desiredFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!currentOwnershipDict.ContainsKey(desired.TargetPath))
            {
                diff.FilesToAdd.Add(desired);
            }
        }

        // 2. 查找需要更新的文件（Desired 和 Current 都有，但内容不同）
        foreach (var desired in desiredFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (currentOwnershipDict.TryGetValue(desired.TargetPath, out var currentOwnership))
            {
                // 检查内容哈希是否一致
                if (!string.IsNullOrEmpty(desired.Hash) &&
                    !string.IsNullOrEmpty(currentOwnership.ContentHash) &&
                    desired.Hash != currentOwnership.ContentHash)
                {
                    diff.FilesToUpdate.Add(desired);
                }
                else if (string.IsNullOrEmpty(desired.Hash) || string.IsNullOrEmpty(currentOwnership.ContentHash))
                {
                    // 如果哈希缺失，检查文件是否实际存在
                    if (!File.Exists(desired.TargetPath))
                    {
                        diff.FilesToUpdate.Add(desired);
                    }
                    else
                    {
                        // 文件存在但哈希未计算，标记为 Unchanged
                        diff.FilesUnchanged.Add(desired);
                    }
                }
                else
                {
                    // 哈希一致，标记为 Unchanged
                    diff.FilesUnchanged.Add(desired);
                }
            }
        }

        // 3. 查找需要删除的文件（Current 中有，Desired 中没有）
        foreach (var currentOwnership in currentOwnershipDict.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!desiredFileDict.ContainsKey(currentOwnership.DeployPath))
            {
                // 检查是否为共享资源
                if (currentOwnership.IsShared && currentOwnership.ReferenceCount > 1)
                {
                    // 共享资源且引用计数 > 1，只减少引用计数
                    _logger?.LogDebug("共享文件 {Path} 引用计数={Count}，不删除",
                        currentOwnership.DeployPath, currentOwnership.ReferenceCount);
                }
                else
                {
                    // 可以安全删除
                    diff.FilesToRemove.Add(new DeployableFileMapping
                    {
                        SourcePath = string.Empty,
                        TargetPath = currentOwnership.DeployPath,
                        Hash = currentOwnership.ContentHash,
                        IsShared = currentOwnership.IsShared,
                        ComponentId = currentOwnership.ComponentId,
                        PackageId = currentOwnership.PackageId
                    });
                }
            }
        }

        _logger?.LogInformation(
            "Diff 计算完成: Add={Add}, Update={Update}, Remove={Remove}, Unchanged={Unchanged}",
            diff.FilesToAdd.Count,
            diff.FilesToUpdate.Count,
            diff.FilesToRemove.Count,
            diff.FilesUnchanged.Count);

        return diff;
    }

    /// <summary>
    /// 判断差异是否为空（无需执行任何操作）
    /// </summary>
    public bool IsEmptyDiff(DeploymentDiff diff)
    {
        return diff.FilesToAdd.Count == 0 &&
               diff.FilesToUpdate.Count == 0 &&
               diff.FilesToRemove.Count == 0;
    }
}

/// <summary>
/// 部署差异结果
/// </summary>
public class DeploymentDiff
{
    /// <summary>
    /// 会话ID
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// 计算时间
    /// </summary>
    public DateTime CalculatedAt { get; set; }

    /// <summary>
    /// 需要添加的文件
    /// </summary>
    public List<DeployableFileMapping> FilesToAdd { get; set; } = new();

    /// <summary>
    /// 需要更新的文件
    /// </summary>
    public List<DeployableFileMapping> FilesToUpdate { get; set; } = new();

    /// <summary>
    /// 需要删除的文件
    /// </summary>
    public List<DeployableFileMapping> FilesToRemove { get; set; } = new();

    /// <summary>
    /// 内容一致的文件（跳过）
    /// </summary>
    public List<DeployableFileMapping> FilesUnchanged { get; set; } = new();

    /// <summary>
    /// 总变更数
    /// </summary>
    public int TotalChanges => FilesToAdd.Count + FilesToUpdate.Count + FilesToRemove.Count;
}
