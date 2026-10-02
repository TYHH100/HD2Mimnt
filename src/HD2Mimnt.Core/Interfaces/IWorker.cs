using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Worker 接口 - 所有后台工作的执行器
/// </summary>
public interface IWorker
{
    /// <summary>
    /// Worker 类型标识
    /// </summary>
    string Type { get; }

    /// <summary>
    /// 执行任务
    /// </summary>
    Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Task 进度报告
/// </summary>
public class TaskProgress
{
    /// <summary>
    /// 进度百分比 (0-100)
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// 当前阶段
    /// </summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>
    /// 当前处理项
    /// </summary>
    public string CurrentItem { get; set; } = string.Empty;

    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
