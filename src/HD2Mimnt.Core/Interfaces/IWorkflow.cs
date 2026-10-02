namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Workflow 接口
/// 定义 Mod 的识别、验证、分析和部署规则
/// </summary>
public interface IWorkflow
{
    /// <summary>
    /// Workflow 名称（Legacy, V1, IntegratedArmor 等）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Workflow 描述
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 检测 Mod 是否符合此 Workflow
    /// </summary>
    Task<bool> CanHandleAsync(string modPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析 Mod 的 Manifest
    /// </summary>
    Task<Entities.ModManifest?> ParseManifestAsync(string modPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证 Mod 结构和文件
    /// </summary>
    Task<WorkflowValidationResult> ValidateAsync(string modPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 Mod 的部署文件列表
    /// </summary>
    /// <param name="modPath">Mod 目录路径</param>
    /// <param name="manifest">已解析的 Manifest（可选）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>需要部署的文件列表（源路径 -> 目标相对路径）</returns>
    Task<List<WorkflowDeploymentFile>> GetDeploymentFilesAsync(
        string modPath,
        Entities.ModManifest? manifest = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 Mod 的清理文件列表
    /// </summary>
    Task<List<string>> GetCleanupFilesAsync(
        string modPath,
        Entities.ModManifest? manifest = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Workflow 验证结果
/// </summary>
public class WorkflowValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Workflow 部署文件
/// </summary>
public class WorkflowDeploymentFile
{
    /// <summary>
    /// 源文件路径（Mod 目录中的绝对路径）
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标相对路径（相对于游戏目录）
    /// </summary>
    public string TargetRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 文件优先级（用于冲突解决）
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 文件元数据
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
