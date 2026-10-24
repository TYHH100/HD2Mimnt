using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Workflow 注册表实现
/// 管理所有可用的 Workflow 实现
/// </summary>
public class WorkflowRegistry : IWorkflowRegistry
{
    private readonly Dictionary<string, IWorkflow> _workflows = new();
    private readonly object _lock = new();

    /// <summary>
    /// 注册一个 Workflow
    /// </summary>
    public void RegisterWorkflow(IWorkflow workflow)
    {
        if (workflow == null)
            throw new ArgumentNullException(nameof(workflow));

        if (string.IsNullOrWhiteSpace(workflow.Name))
            throw new ArgumentException("Workflow name cannot be null or empty.", nameof(workflow));

        lock (_lock)
        {
            _workflows[workflow.Name] = workflow;
        }
    }

    /// <summary>
    /// 注销一个 Workflow
    /// </summary>
    public void UnregisterWorkflow(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("Workflow name cannot be null or empty.", nameof(workflowName));

        lock (_lock)
        {
            _workflows.Remove(workflowName);
        }
    }

    /// <summary>
    /// 获取指定名称的 Workflow
    /// </summary>
    public IWorkflow? GetWorkflow(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            return null;

        lock (_lock)
        {
            return _workflows.TryGetValue(workflowName, out var workflow) ? workflow : null;
        }
    }

    /// <summary>
    /// 获取所有已注册的 Workflow
    /// </summary>
    public IEnumerable<IWorkflow> GetAllWorkflows()
    {
        lock (_lock)
        {
            return _workflows.Values.ToList();
        }
    }

    /// <summary>
    /// 检测 Mod 适用的 Workflow
    /// </summary>
    public async Task<IWorkflow?> DetectWorkflowAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath))
            return null;

        IWorkflow[] workflows;
        lock (_lock)
        {
            workflows = _workflows.Values.ToArray();
        }

        foreach (var workflow in workflows)
        {
            if (await workflow.CanHandleAsync(modPath, cancellationToken))
            {
                return workflow;
            }
        }

        return null;
    }

    /// <summary>
    /// 检测 Mod 适用的所有 Workflow
    /// </summary>
    public async Task<List<IWorkflow>> DetectAllWorkflowsAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath))
            return new List<IWorkflow>();

        IWorkflow[] workflows;
        lock (_lock)
        {
            workflows = _workflows.Values.ToArray();
        }

        var matchedWorkflows = new List<IWorkflow>();

        foreach (var workflow in workflows)
        {
            if (await workflow.CanHandleAsync(modPath, cancellationToken))
            {
                matchedWorkflows.Add(workflow);
            }
        }

        return matchedWorkflows;
    }
}
