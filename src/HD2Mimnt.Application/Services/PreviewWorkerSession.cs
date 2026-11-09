using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.IPC;
using HD2Mimnt.Core.IPC;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 预览会话进程：预览打开时按需启动 HD2Mimnt.Worker.exe 的 Preview 会话，关闭预览时终止进程。
/// 与一次性任务 Worker 不同，这里保持长连接按请求/响应交互；进程退出即归还解析缓冲、
/// 解码像素与音频缓存。同时只服务一个预览会话（模型/音频预览通过引用计数共享进程）。
/// </summary>
public sealed class PreviewWorkerSession : IPreviewWorkerSession, IDisposable
{
    private readonly string _workerExecutablePath;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _shutdownGracePeriod;
    private readonly ILogger<PreviewWorkerSession>? _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PreviewResponsePayload>> _pending = new();

    private NamedPipeServer? _pipe;
    private Process? _process;
    private Process? _startingProcess;
    private Guid _taskId;
    private Task? _startTask;
    private Task? _readerTask;
    private int _generation;
    private int _users;
    private bool _disposed;

    /// <summary>Worker 推送的播放状态/结束事件，在会话读取线程触发。</summary>
    public event Action<PreviewEventPayload>? EventReceived;

    /// <summary>会话进程是否存活（测试与诊断用）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    public PreviewWorkerSession(ILogger<PreviewWorkerSession>? logger = null,
        string? workerExecutablePath = null, TimeSpan? connectTimeout = null,
        TimeSpan? shutdownGracePeriod = null)
    {
        _logger = logger;
        _workerExecutablePath = workerExecutablePath
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2Mimnt.Worker.exe");
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15);
        _shutdownGracePeriod = shutdownGracePeriod ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>进入预览：模型/音频预览打开时调用，和 <see cref="ExitAsync"/> 成对使用。</summary>
    public void Enter() => Interlocked.Increment(ref _users);

    /// <summary>离开预览：最后一个使用者离开时终止 Worker 进程。</summary>
    public async Task ExitAsync()
    {
        var remaining = Interlocked.Decrement(ref _users);
        if (remaining > 0) return;
        Interlocked.Exchange(ref _users, 0);
        await StopAsync();
    }

    public async Task<TResponse?> RequestAsync<TResponse>(string method, object? payload,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(method, payload, cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? $"Preview worker request '{method}' failed.");
        return response.Payload is null ? default : JsonSerializer.Deserialize<TResponse>(response.Payload);
    }

    /// <summary>不需要等待结果的控制命令（暂停/停止/音量等）；失败仅记录日志。</summary>
    public void Post(string method, object? payload = null)
    {
        if (Volatile.Read(ref _users) <= 0) return;
        lock (_gate)
        {
            // 控制命令不能顺手拉起进程：预览尚未加载时 Worker 不在，命令也无处可去。
            if (_pipe is null) return;
        }
        _ = PostCoreAsync(method, payload);
    }

    private async Task PostCoreAsync(string method, object? payload)
    {
        try
        {
            await SendRequestAsync(method, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Preview command {Method} failed", method);
        }
    }

    private async Task<PreviewResponsePayload> SendRequestAsync(string method, object? payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(cancellationToken);

        NamedPipeServer pipe;
        Guid taskId;
        lock (_gate)
        {
            pipe = _pipe ?? throw new InvalidOperationException("Preview worker is not connected.");
            taskId = _taskId;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PreviewResponsePayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            await pipe.SendMessageAsync(new WorkerMessage
            {
                MessageType = PreviewMessageTypes.Request,
                TaskId = taskId,
                Payload = JsonSerializer.Serialize(new PreviewRequestPayload
                {
                    RequestId = requestId,
                    Method = method,
                    Payload = payload is null ? null : JsonSerializer.Serialize(payload)
                })
            }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Worker 顺序执行请求；取消当前请求后它才能接收下一个请求。
            _ = NotifyCancellationAsync(pipe, taskId);
            throw;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task NotifyCancellationAsync(NamedPipeServer pipe, Guid taskId)
    {
        try
        {
            await pipe.SendMessageAsync(new WorkerMessage
            {
                MessageType = PreviewMessageTypes.Cancel,
                TaskId = taskId
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Worker 已退出
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        Task startTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipe is not null && _process is { HasExited: false }) return;
            _startTask ??= StartCoreAsync();
            startTask = _startTask;
        }
        await startTask.WaitAsync(cancellationToken);
    }

    private async Task StartCoreAsync()
    {
        Process? process = null;
        NamedPipeServer? pipe = null;
        var taskId = Guid.NewGuid();
        var pipeName = $"HD2Mimnt.Preview.{taskId:N}";
        int generation;
        lock (_gate)
        {
            generation = _generation;
        }
        try
        {
            pipe = new NamedPipeServer(pipeName);
            process = Process.Start(new ProcessStartInfo
            {
                FileName = _workerExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "--task-id", taskId.ToString(),
                    "--pipe-name", pipeName
                }
            }) ?? throw new InvalidOperationException("Failed to start preview worker process.");
            lock (_gate) _startingProcess = process;
            _ = CaptureStandardErrorAsync(process);

            using var connectCts = new CancellationTokenSource(_connectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Preview worker failed to connect within timeout.");
            }
            await pipe.SendMessageAsync(new WorkerMessage
            {
                MessageType = "Command",
                TaskId = taskId,
                Payload = JsonSerializer.Serialize(new ExecuteWorkerCommand { WorkerType = "Preview" })
            }, CancellationToken.None);

            lock (_gate)
            {
                // 启动期间会话已被关闭：丢弃刚连上的进程，避免留下后台会话。
                if (_disposed || generation != _generation)
                    throw new OperationCanceledException("Preview session closed during startup.");
                _pipe = pipe;
                _process = process;
                _taskId = taskId;
                _readerTask = Task.Run(() => ReadLoopAsync(taskId, pipe));
                _startingProcess = null;
            }
            _logger?.LogDebug("Preview worker {TaskId} started", taskId);
        }
        catch
        {
            Kill(process);
            pipe?.Dispose();
            process?.Dispose();
            lock (_gate) _startingProcess = null;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation) _startTask = null;
            }
        }
    }

    private async Task ReadLoopAsync(Guid taskId, NamedPipeServer pipe)
    {
        try
        {
            while (true)
            {
                var message = await pipe.ReceiveMessageAsync(CancellationToken.None);
                if (message is null) break;
                switch (message.MessageType)
                {
                    case PreviewMessageTypes.Response:
                        var response = JsonSerializer.Deserialize<PreviewResponsePayload>(message.Payload ?? "{}");
                        if (response is not null &&
                            _pending.TryRemove(response.RequestId, out var completion))
                            completion.TrySetResult(response);
                        break;
                    case PreviewMessageTypes.Event:
                        var previewEvent = JsonSerializer.Deserialize<PreviewEventPayload>(message.Payload ?? "{}");
                        if (previewEvent is not null)
                        {
                            try { EventReceived?.Invoke(previewEvent); }
                            catch (Exception ex) { _logger?.LogWarning(ex, "Preview event handler failed"); }
                        }
                        break;
                    case "Error":
                        var workerError = JsonSerializer.Deserialize<WorkerErrorResponse>(message.Payload ?? "{}");
                        FailPending(new InvalidOperationException(
                            workerError?.ErrorMessage ?? "Preview worker failed."));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Preview worker {TaskId} read loop failed", taskId);
        }
        finally
        {
            FailPending(new InvalidOperationException("Preview worker process exited."));
            var isCurrentSession = false;
            lock (_gate)
            {
                if (ReferenceEquals(_pipe, pipe))
                {
                    _pipe = null;
                    _process = null;
                    isCurrentSession = true;
                }
            }
            if (isCurrentSession)
            {
                try
                {
                    EventReceived?.Invoke(new PreviewEventPayload
                    {
                        EventType = PreviewEventTypes.SessionEnded
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Preview session ended handler failed");
                }
            }
        }
    }

    private async Task StopAsync()
    {
        NamedPipeServer? pipe;
        Process? process;
        Guid taskId;
        Task? readerTask;
        lock (_gate)
        {
            _generation++;
            _startTask = null;
            pipe = _pipe;
            process = _process;
            taskId = _taskId;
            readerTask = _readerTask;
            _pipe = null;
            _process = null;
            _readerTask = null;
            Kill(_startingProcess);
            _startingProcess = null;
        }

        FailPending(new OperationCanceledException("Preview session closed."));
        if (pipe is not null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(_shutdownGracePeriod);
                await pipe.SendMessageAsync(new WorkerMessage
                {
                    MessageType = PreviewMessageTypes.Shutdown,
                    TaskId = taskId
                }, shutdownCts.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        if (process is not null)
        {
            try
            {
                using var exitCts = new CancellationTokenSource(_shutdownGracePeriod);
                await process.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to terminate preview worker process");
            }
            finally
            {
                process.Dispose();
            }
        }

        // 读取循环会在管道释放后退出；短暂等待可避免旧会话的清理覆盖新会话状态。
        if (readerTask is not null)
        {
            try { await readerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
        pipe?.Dispose();
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
                completion.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _users, 0);
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to dispose preview worker session");
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task CaptureStandardErrorAsync(Process process)
    {
        var buffer = new StringBuilder();
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (buffer)
                {
                    buffer.AppendLine(line);
                    if (buffer.Length > 8000) buffer.Remove(0, buffer.Length - 4000);
                }
            }
        }
        catch
        {
        }
    }
}
