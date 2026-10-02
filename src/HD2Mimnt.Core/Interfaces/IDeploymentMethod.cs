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
    /// 清理已部署的文件
    /// </summary>
    Task<bool> CleanupFileAsync(
        string targetPath,
        CancellationToken cancellationToken = default);
}
