namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Workflow 注册表接口
/// 管理所有可用的 Workflow 实现
/// </summary>
public interface IWorkflowRegistry
{
    /// <summary>
    /// 注册一个 Workflow
    /// </summary>
    void RegisterWorkflow(IWorkflow workflow);

    /// <summary>
    /// 注销一个 Workflow
    /// </summary>
    void UnregisterWorkflow(string workflowName);

    /// <summary>
    /// 获取指定名称的 Workflow
    /// </summary>
    IWorkflow? GetWorkflow(string workflowName);

    /// <summary>
    /// 获取所有已注册的 Workflow
    /// </summary>
    IEnumerable<IWorkflow> GetAllWorkflows();

    /// <summary>
    /// 检测 Mod 适用的 Workflow
    /// </summary>
    /// <param name="modPath">Mod 目录路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>第一个匹配的 Workflow，如果没有匹配则返回 null</returns>
    Task<IWorkflow?> DetectWorkflowAsync(string modPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检测 Mod 适用的所有 Workflow
    /// </summary>
    Task<List<IWorkflow>> DetectAllWorkflowsAsync(string modPath, CancellationToken cancellationToken = default);
}
