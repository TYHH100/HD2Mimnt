namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Progress information for worker tasks
/// </summary>
public class WorkerTaskProgress
{
    public double Progress { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string CurrentItem { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Result from worker execution
/// </summary>
public class WorkerResult
{
    public bool Success { get; set; }
    public bool PartiallyFailed { get; set; }
    public bool Cancelled { get; set; }
    public string? Result { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Crashed { get; set; }
}

/// <summary>
/// Manages worker processes and IPC communication
/// </summary>
public interface IWorkerHostManager
{
    /// <summary>
    /// Start a worker in a separate process; taskId 省略时生成新 Id，
    /// 传入时沿用任务中心已创建的任务 Id。返回实际使用的 TaskId。
    /// </summary>
    Task<Guid> StartWorkerAsync(
        string workerType,
        Dictionary<string, string> arguments,
        IProgress<WorkerTaskProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Guid? taskId = null);

    /// <summary>
    /// Check if a worker is currently running
    /// </summary>
    Task<bool> IsWorkerRunningAsync(Guid taskId);

    /// <summary>
    /// Cancel a running worker：先发送 Cancel 消息等待优雅退出，超时后 Kill 兜底。
    /// </summary>
    Task CancelWorkerAsync(Guid taskId);

    /// <summary>
    /// Force terminate a worker process (kill)
    /// </summary>
    Task ForceTerminateWorkerAsync(Guid taskId);

    /// <summary>
    /// Wait for worker completion and get result
    /// </summary>
    Task<WorkerResult> WaitForCompletionAsync(Guid taskId, CancellationToken cancellationToken = default);
}
