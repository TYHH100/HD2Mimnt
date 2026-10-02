namespace HD2Mimnt.Core.IPC;

/// <summary>
/// Base message format for IPC communication between main app and worker process
/// </summary>
public class WorkerMessage
{
    /// <summary>
    /// Type of message: "Command", "Response", "Progress", "Error", "Completed"
    /// </summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>
    /// Task ID this message belongs to
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// JSON-serialized payload
    /// </summary>
    public string? Payload { get; set; }
}

/// <summary>
/// Command to execute a worker
/// </summary>
public class ExecuteWorkerCommand
{
    /// <summary>
    /// Type of worker: "Import", "Scan", "Validation"
    /// </summary>
    public string WorkerType { get; set; } = string.Empty;

    /// <summary>
    /// Arguments for the worker (key-value pairs)
    /// </summary>
    public Dictionary<string, string> Arguments { get; set; } = new();
}

/// <summary>
/// Progress update from worker
/// </summary>
public class WorkerProgressResponse
{
    /// <summary>
    /// Current step number
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// Total number of steps
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// Progress message
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Worker completion response
/// </summary>
public class WorkerCompletedResponse
{
    /// <summary>
    /// Whether the worker completed successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result data (JSON-serialized)
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// List of errors that occurred
    /// </summary>
    public List<string>? Errors { get; set; }
}

/// <summary>
/// Worker error response
/// </summary>
public class WorkerErrorResponse
{
    /// <summary>
    /// Error message
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Stack trace (if available)
    /// </summary>
    public string? StackTrace { get; set; }
}
