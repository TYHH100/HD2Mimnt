using System.Collections.Concurrent;
using System.Text.Json;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Worker.IPC;

namespace HD2Mimnt.Worker;

/// <summary>
/// Worker 进程侧 IPC 门面：接收执行命令与取消信号，向主程序回传进度和终态。
/// </summary>
public class WorkerHost : IWorkerTaskHost, IDisposable
{
    private readonly Guid _taskId;
    private readonly NamedPipeClient _pipeClient;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerPromptResponse>> _pendingPrompts = new();

    public Guid TaskId => _taskId;

    /// <summary>主程序发来 Cancel 消息时触发；业务代码用它取消实际工作。</summary>
    public CancellationToken CancellationRequested => _cancellation.Token;

    public WorkerHost(Guid taskId, string pipeName)
    {
        _taskId = taskId;
        _pipeClient = new NamedPipeClient(pipeName);
    }

    public async Task ConnectAsync(int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default)
    {
        await _pipeClient.ConnectAsync(timeoutMilliseconds, cancellationToken);
    }

    public async Task<ExecuteWorkerCommand?> ReceiveCommandAsync(CancellationToken cancellationToken = default)
    {
        var message = await _pipeClient.ReceiveMessageAsync(cancellationToken);
        if (message == null || message.MessageType != "Command")
        {
            return null;
        }

        return JsonSerializer.Deserialize<ExecuteWorkerCommand>(message.Payload ?? "{}");
    }

    /// <summary>预览会话等长连接 Worker 自行读取后续消息；一次性任务不要调用。</summary>
    public Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        => _pipeClient.ReceiveMessageAsync(cancellationToken);

    /// <summary>发送任意会话消息（预览响应/事件）；与进度回执共用同一发送闸门。</summary>
    public Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
        => _pipeClient.SendMessageAsync(message, cancellationToken);

    /// <summary>
    /// 后台监听主程序的后续消息：收到 Cancel 触发取消令牌；对端关闭即结束。
    /// 与业务执行同生命周期，由 Dispose 停止。
    /// </summary>
    public async Task WatchForCancellationAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var message = await _pipeClient.ReceiveMessageAsync(_cancellation.Token);
                if (message == null) return;
                if (message.MessageType == "Cancel")
                {
                    _cancellation.Cancel();
                    return;
                }

                if (message.MessageType == "PromptResponse")
                {
                    var response = JsonSerializer.Deserialize<WorkerPromptResponse>(message.Payload ?? "{}");
                    if (response != null && _pendingPrompts.TryRemove(response.RequestId, out var pending))
                        pending.TrySetResult(response);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public Task SendProgressAsync(double progress, string stage, string? currentItem = null,
        string? message = null, CancellationToken cancellationToken = default)
    {
        var payload = new WorkerProgressResponse
        {
            Progress = progress,
            Stage = stage,
            CurrentItem = currentItem ?? string.Empty,
            Message = message ?? string.Empty
        };
        return SendAsync("Progress", payload, cancellationToken);
    }

    public Task SendCompletedAsync(bool success, string? result = null, List<string>? errors = null,
        bool partiallyFailed = false, CancellationToken cancellationToken = default, string? data = null)
    {
        var payload = new WorkerCompletedResponse
        {
            Success = success,
            PartiallyFailed = partiallyFailed,
            Result = result,
            Errors = errors,
            Data = data
        };
        return SendAsync("Completed", payload, cancellationToken);
    }

    /// <summary>
    /// 请求主程序交互（例如询问压缩包密码）；主程序在 UI 线程处理后回传结果。
    /// 任务取消时抛出 OperationCanceledException，业务按取消收尾。
    /// </summary>
    public async Task<WorkerPromptResponse> PromptAsync(string kind, string? title = null, string? payload = null,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<WorkerPromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPrompts[requestId] = completion;
        try
        {
            await SendAsync("Prompt", new WorkerPromptRequest
            {
                RequestId = requestId,
                Kind = kind,
                Title = title,
                Payload = payload
            }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pendingPrompts.TryRemove(requestId, out _);
        }
    }

    /// <summary>业务已响应取消并清理完毕后的终态回执。</summary>
    public Task SendCancelledAsync(CancellationToken cancellationToken = default)
        => SendAsync<object?>("Cancelled", null, cancellationToken);

    public Task SendErrorAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        var payload = new WorkerErrorResponse
        {
            ErrorMessage = exception.Message,
            StackTrace = exception.StackTrace
        };
        return SendAsync("Error", payload, cancellationToken);
    }

    private Task SendAsync<T>(string messageType, T payload, CancellationToken cancellationToken)
    {
        var message = new WorkerMessage
        {
            MessageType = messageType,
            TaskId = _taskId,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload)
        };
        return _pipeClient.SendMessageAsync(message, cancellationToken);
    }

    public void Dispose()
    {
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        _pipeClient.Dispose();
        _cancellation.Dispose();
    }
}