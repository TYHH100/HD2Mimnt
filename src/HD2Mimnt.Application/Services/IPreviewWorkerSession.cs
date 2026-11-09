using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Application.Services;

/// <summary>预览 Worker 会话的抽象，便于 UI/远程读取器不直接依赖进程实现。</summary>
public interface IPreviewWorkerSession
{
    /// <summary>Worker 推送的播放状态/结束事件，在会话读取线程触发。</summary>
    event Action<PreviewEventPayload>? EventReceived;

    /// <summary>进入预览（引用计数）。</summary>
    void Enter();

    /// <summary>离开预览；最后一个使用者离开时终止 Worker 进程。</summary>
    Task ExitAsync();

    Task<TResponse?> RequestAsync<TResponse>(string method, object? payload,
        CancellationToken cancellationToken = default);

    /// <summary>不需要等待结果的控制命令。</summary>
    void Post(string method, object? payload = null);
}
