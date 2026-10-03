namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 可部署模组源接口
/// 统一抽象传统 Mod 和 LogicalMod 的部署来源
/// </summary>
public interface IDeployableModSource
{
    /// <summary>
    /// 模组唯一标识
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// 模组名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 模组版本
    /// </summary>
    string Version { get; }

    /// <summary>
    /// 模组作者
    /// </summary>
    string Author { get; }

    /// <summary>
    /// 模组类型（Traditional 或 LogicalMod）
    /// </summary>
    string ModType { get; }

    /// <summary>
    /// 获取所有可部署文件
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>可部署文件映射列表（源路径 -> 目标路径）</returns>
    Task<IEnumerable<DeployableFileMapping>> GetDeployableFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取部署前的依赖项
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>依赖的模组ID列表</returns>
    Task<IEnumerable<Guid>> GetDependenciesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 可部署文件映射
/// </summary>
public class DeployableFileMapping
{
    /// <summary>
    /// 源文件路径（可能在归档中）
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标部署路径（相对于游戏目录）
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 文件内容哈希
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// 是否为共享资源
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// 所属组件ID（LogicalMod 使用）
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// 所属包ID（LogicalMod 使用）
    /// </summary>
    public Guid? PackageId { get; set; }

    /// <summary>
    /// 是否需要从归档提取
    /// </summary>
    public bool RequiresExtraction { get; set; }

    /// <summary>
    /// 归档内部路径（如果需要提取）
    /// </summary>
    public string? ArchiveInternalPath { get; set; }
}
