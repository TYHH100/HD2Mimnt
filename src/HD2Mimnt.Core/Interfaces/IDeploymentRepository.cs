using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 部署记录仓储接口
/// 用于记录和查询部署历史
/// </summary>
public interface IDeploymentRepository
{
    /// <summary>
    /// 获取部署记录
    /// </summary>
    Task<DeploymentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定会话的所有部署记录
    /// </summary>
    Task<IEnumerable<DeploymentRecord>> GetBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定 Mod 的所有部署记录
    /// </summary>
    Task<IEnumerable<DeploymentRecord>> GetByModIdAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取最近的部署会话
    /// </summary>
    Task<Guid?> GetLatestSessionIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取最近的部署记录
    /// </summary>
    Task<IEnumerable<DeploymentRecord>> GetLatestDeploymentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加部署记录
    /// </summary>
    Task AddAsync(DeploymentRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新部署记录
    /// </summary>
    Task UpdateAsync(DeploymentRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除部署记录
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定会话的所有部署记录
    /// </summary>
    Task DeleteBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定 Mod 的所有部署记录
    /// </summary>
    Task DeleteByModIdAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加部署文件记录
    /// </summary>
    Task AddDeploymentFileAsync(DeploymentFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量添加部署文件记录
    /// </summary>
    Task AddDeploymentFilesAsync(IEnumerable<DeploymentFile> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取部署记录的所有文件
    /// </summary>
    Task<IEnumerable<DeploymentFile>> GetDeploymentFilesAsync(Guid deploymentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定目标路径的部署文件
    /// </summary>
    Task<DeploymentFile?> GetDeploymentFileByTargetPathAsync(string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定部署记录的所有文件记录
    /// </summary>
    Task DeleteDeploymentFilesByDeploymentIdAsync(Guid deploymentId, CancellationToken cancellationToken = default);
}
