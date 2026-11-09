using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 部署方法接口
/// Copy, Symbolic Link, Hard Link 都实现此接口
/// </summary>
public interface IDeploymentMethod
{
    /// <summary>
    /// 部署方法名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 检查是否可以使用此部署方法
    /// 例如：Symbolic Link 需要管理员权限，Hard Link 需要同一卷
    /// </summary>
    Task<(bool CanDeploy, string? ErrorMessage)> CheckCompatibilityAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 部署单个文件
    /// </summary>
    Task<bool> DeployFileAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断目标是否已经是该方法期望的状态：Copy 比较内容，HardLink 比较文件身份，
    /// SymbolicLink 比较链接目标。目标缺失或无法读取时返回 false。
    /// </summary>
    Task<bool> IsUpToDateAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理已部署的文件
    /// </summary>
    Task<bool> CleanupFileAsync(
        string targetPath,
        CancellationToken cancellationToken = default);
}
