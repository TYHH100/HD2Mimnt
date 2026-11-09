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

    /// <summary>获取指定游戏安装的当前部署状态（每模组一行）。</summary>
    Task<IReadOnlyList<DeploymentRecord>> GetCurrentDeploymentsAsync(string gamePath, CancellationToken cancellationToken = default);

    /// <summary>获取指定游戏安装中某个模组的当前部署状态。</summary>
    Task<DeploymentRecord?> GetCurrentDeploymentAsync(string gamePath, Guid modId, CancellationToken cancellationToken = default);

    /// <summary>获取指定游戏安装的全部当前部署文件。</summary>
    Task<IReadOnlyList<DeploymentFile>> GetFilesForGameAsync(string gamePath, CancellationToken cancellationToken = default);

    /// <summary>获取某个模组在所有游戏安装中的部署文件。</summary>
    Task<IReadOnlyList<DeploymentFile>> GetFilesByModIdAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加部署记录
    /// </summary>
    Task AddAsync(DeploymentRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新部署记录
    /// </summary>
    Task UpdateAsync(DeploymentRecord record, CancellationToken cancellationToken = default);

    /// <summary>按 (GamePath, ModId) 原地更新或插入当前状态记录，返回实际记录 ID。</summary>
    Task<Guid> UpsertAsync(DeploymentRecord record, CancellationToken cancellationToken = default);

    /// <summary>在单个事务内替换某个部署记录的全部文件行。</summary>
    Task ReplaceDeploymentFilesAsync(Guid deploymentId, IReadOnlyList<DeploymentFile> files,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 单事务提交一次对账结果：upsert 当前记录及文件、删除不再存在的记录。
    /// 不依赖 SQLite 外键级联。
    /// </summary>
    Task SaveReconciledStateAsync(string gamePath, IReadOnlyList<DeploymentRecord> records,
        IReadOnlyCollection<Guid> removedDeploymentIds, CancellationToken cancellationToken = default);

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

    /// <summary>原子提交删除模组部署记录与剩余补丁的新目标路径。</summary>
    Task CommitModDeploymentRemovalAsync(IReadOnlyCollection<Guid> deploymentIds,
        IReadOnlyList<(Guid FileId, string TargetPath)> renamedFiles, CancellationToken cancellationToken = default);
}
