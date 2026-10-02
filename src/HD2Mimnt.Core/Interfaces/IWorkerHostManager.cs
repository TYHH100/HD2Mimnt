namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Progress information for worker tasks
/// </summary>
public class WorkerTaskProgress
{
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Result from worker execution
/// </summary>
public class WorkerResult
{
    public bool Success { get; set; }
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
    /// Start a worker in a separate process
    /// </summary>
    Task<Guid> StartWorkerAsync(
        string workerType,
        Dictionary<string, string> arguments,
        IProgress<WorkerTaskProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a worker is currently running
    /// </summary>
    Task<bool> IsWorkerRunningAsync(Guid taskId);

    /// <summary>
    /// Cancel a running worker (graceful shutdown)
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
