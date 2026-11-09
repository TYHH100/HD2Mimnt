namespace HD2Mimnt.Core.IPC;

/// <summary>
/// Base message format for IPC communication between main app and worker process
/// </summary>
public class WorkerMessage
{
    /// <summary>
    /// Type of message: "Command", "Cancel", "Cancelled", "Progress", "Error", "Completed", "Prompt", "PromptResponse"
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
    /// Type of worker: "Validation"
    /// </summary>
    public string WorkerType { get; set; } = string.Empty;

    /// <summary>
    /// Arguments for the worker (key-value pairs)
    /// </summary>
    public Dictionary<string, string> Arguments { get; set; } = new();
}

/// <summary>
/// Progress update from worker; fields mirror <see cref="Interfaces.TaskProgress"/>.
/// </summary>
public class WorkerProgressResponse
{
    public double Progress { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string CurrentItem { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
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
    /// 部分成功（如批量校验存在失败项）；主程序映射为 PartiallyFailed。
    /// </summary>
    public bool PartiallyFailed { get; set; }

    /// <summary>
    /// Result data (JSON-serialized)
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// List of errors that occurred
    /// </summary>
    public List<string>? Errors { get; set; }

    /// <summary>
    /// 任务私有结果数据（JSON），例如批量导入的模组 ID 列表；主程序按任务类型解析。
    /// </summary>
    public string? Data { get; set; }
}

/// <summary>
/// Worker 向主程序发起的交互请求（密码、确认等）。
/// Worker 里不能弹窗，必须先经 IPC 询问主程序，主程序用 WorkerPromptResponse 回复。
/// </summary>
public class WorkerPromptRequest
{
    public string RequestId { get; set; } = string.Empty;

    /// <summary>"ArchivePassword" / "Hd2IrConfirm"</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>交互请求的 JSON 载荷（如 Hd2IrImportPreview）。</summary>
    public string? Payload { get; set; }
}

/// <summary>主程序对 WorkerPromptRequest 的回复。</summary>
public class WorkerPromptResponse
{
    public string RequestId { get; set; } = string.Empty;

    /// <summary>用户是否确认/提供了输入；拒绝时 Worker 按取消或失败处理。</summary>
    public bool Accepted { get; set; }

    /// <summary>用户输入（当前用于压缩包密码）。</summary>
    public string? Value { get; set; }
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
