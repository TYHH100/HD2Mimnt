using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.IPC;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 管理 HD2Mimnt.Worker 独立进程：启动、进度转发、优雅取消、强杀与崩溃识别。
/// 每个任务一个进程，Worker 卡死或崩溃不影响主程序。
/// </summary>
public class WorkerHostManager : IWorkerHostManager, IDisposable
{
    private readonly ConcurrentDictionary<Guid, WorkerProcess> _runningWorkers = new();
    private readonly ConcurrentDictionary<Guid, Task<WorkerResult>> _completions = new();
    private readonly string _workerExecutablePath;
    private readonly TimeSpan _cancelGracePeriod;
    private readonly TimeSpan _connectTimeout;
    private readonly ILogger<WorkerHostManager>? _logger;
    private bool _disposed;

    public WorkerHostManager(
        ILogger<WorkerHostManager>? logger = null,
        string? workerExecutablePath = null,
        TimeSpan? cancelGracePeriod = null,
        TimeSpan? connectTimeout = null)
    {
        _logger = logger;
        _cancelGracePeriod = cancelGracePeriod ?? TimeSpan.FromSeconds(5);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        // Worker.exe 与主程序同目录（发布流程保证）；测试可注入独立宿主路径。
        _workerExecutablePath = workerExecutablePath
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2Mimnt.Worker.exe");
    }

    public async Task<Guid> StartWorkerAsync(
        string workerType,
        Dictionary<string, string> arguments,
        IProgress<WorkerTaskProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Guid? taskId = null,
        WorkerPromptHandler? promptHandler = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = taskId ?? Guid.NewGuid();
        var pipeName = $"HD2Mimnt.Worker.{id}";
        var pipeServer = new NamedPipeServer(pipeName);
        Process? process = null;

        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _workerExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("--task-id");
            processStartInfo.ArgumentList.Add(id.ToString());
            processStartInfo.ArgumentList.Add("--pipe-name");
            processStartInfo.ArgumentList.Add(pipeName);

            process = Process.Start(processStartInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start worker process");
            }

            // stderr 必须持续读走，否则缓冲区写满会让子进程阻塞；崩溃时作为诊断信息附到错误里。
            var standardError = new StringBuilder();
            var standardErrorCapture = CaptureStandardErrorAsync(process, standardError);

            // 连接等待：外部取消与超时分开判定，失败路径统一清理进程与管道
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_connectTimeout);
            try
            {
                await pipeServer.WaitForConnectionAsync(connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Worker process failed to connect within timeout");
            }

            var command = new ExecuteWorkerCommand
            {
                WorkerType = workerType,
                Arguments = arguments
            };
            await pipeServer.SendMessageAsync(new WorkerMessage
            {
                MessageType = "Command",
                TaskId = id,
                Payload = JsonSerializer.Serialize(command)
            }, cancellationToken);

            var worker = new WorkerProcess(id, process, pipeServer, progress, promptHandler,
                standardError, standardErrorCapture);
            // 结果缓存与进程生命周期解耦：Worker 可能在调用方取结果前就崩溃并清理完毕。
            _completions[id] = worker.CompletionTask;
            if (!_runningWorkers.TryAdd(id, worker))
            {
                worker.KillAndDispose();
                throw new InvalidOperationException($"Worker {id} is already running");
            }

            // 监听器与工作者同生命周期，不随启动调用方令牌取消
            _ = ListenForMessagesAsync(worker);
            return id;
        }
        catch
        {
            KillProcess(process);
            process?.Dispose();
            pipeServer.Dispose();
            throw;
        }
    }

    private async Task ListenForMessagesAsync(WorkerProcess worker)
    {
        try
        {
            while (!worker.ListenerCts.IsCancellationRequested)
            {
                var message = await worker.Pipe.ReceiveMessageAsync(worker.ListenerCts.Token);
                if (message == null) break;

                switch (message.MessageType)
                {
                    case "Progress":
                        var progressData = JsonSerializer.Deserialize<WorkerProgressResponse>(message.Payload ?? "{}");
                        if (progressData != null)
                        {
                            worker.Progress?.Report(new WorkerTaskProgress
                            {
                                Progress = progressData.Progress,
                                Stage = progressData.Stage,
                                CurrentItem = progressData.CurrentItem,
                                Message = progressData.Message
                            });
                        }
                        break;

                    case "Completed":
                        var completedData = JsonSerializer.Deserialize<WorkerCompletedResponse>(message.Payload ?? "{}");
                        worker.SetResult(completedData?.Success ?? false, completedData?.Result,
                            completedData?.Errors, completedData?.PartiallyFailed ?? false, completedData?.Data);
                        return;

                    case "Prompt":
                        // 交互请求要等用户操作，不能阻塞监听循环（否则进度与取消都收不到）。
                        // 不带 Handler 的 Worker 直接拒绝，让业务按缺少输入失败。
                        var promptData = JsonSerializer.Deserialize<WorkerPromptRequest>(message.Payload ?? "{}");
                        if (promptData != null) _ = RespondToPromptAsync(worker, promptData);
                        break;

                    case "Cancelled":
                        worker.SetCancelled();
                        return;

                    case "Error":
                        var errorData = JsonSerializer.Deserialize<WorkerErrorResponse>(message.Payload ?? "{}");
                        worker.SetError(errorData?.ErrorMessage ?? "Unknown worker error");
                        return;
                }
            }

            // 无终态回执且管道关闭：进程退出（崩溃/被外部杀死）必须判为崩溃，
            // 否则 WaitForCompletionAsync 会永久等待。
            if (!worker.CompletionTask.IsCompleted)
            {
                await worker.Process.WaitForExitAsync(worker.ListenerCts.Token);
                // 退出瞬间 stderr 可能还有末尾几行未读完，短暂等待后拼进错误信息
                try { await worker.StandardErrorCapture.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
                worker.SetCrashed(
                    $"Worker process exited unexpectedly (exit code {worker.Process.ExitCode}){worker.StandardErrorTail}");
            }
        }
        catch (OperationCanceledException)
        {
            // 取消路径：不当作崩溃记录，统一走 finally 清理
        }
        catch (Exception ex)
        {
            worker.SetCrashed(ex.Message);
        }
        finally
        {
            CleanupWorker(worker);
        }
    }

    private async Task RespondToPromptAsync(WorkerProcess worker, WorkerPromptRequest request)
    {
        var response = new WorkerPromptResponse { RequestId = request.RequestId, Accepted = false };
        if (worker.PromptHandler != null)
        {
            try
            {
                response = await worker.PromptHandler(request, worker.ListenerCts.Token);
                response.RequestId = request.RequestId;
            }
            catch (OperationCanceledException)
            {
                // Worker 已结束或任务取消，不需要回复
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "处理 Worker 交互请求失败 {TaskId}/{Kind}", worker.TaskId, request.Kind);
            }
        }

        try
        {
            await worker.Pipe.SendMessageAsync(new WorkerMessage
            {
                MessageType = "PromptResponse",
                TaskId = worker.TaskId,
                Payload = JsonSerializer.Serialize(response)
            }, worker.ListenerCts.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Worker 已退出，回复无处可去
        }
    }

    private void CleanupWorker(WorkerProcess worker)
    {
        worker.ListenerCts.Cancel();
        KillProcess(worker.Process);
        worker.Dispose();
        _runningWorkers.TryRemove(worker.TaskId, out _);
        worker.MarkCleaned();
    }

    public Task<bool> IsWorkerRunningAsync(Guid taskId)
    {
        return Task.FromResult(_runningWorkers.ContainsKey(taskId));
    }

    /// <summary>发送 Cancel 消息等待优雅退出；宽限期后仍未结束则 Kill 兜底。</summary>
    public async Task CancelWorkerAsync(Guid taskId)
    {
        if (!_runningWorkers.TryGetValue(taskId, out var worker)) return;

        try
        {
            // 对端卡死时写管道不能无限等待：发送本身也受宽限期约束，超时直接进入 Kill 分支。
            using var sendCts = new CancellationTokenSource(_cancelGracePeriod);
            await worker.Pipe.SendMessageAsync(new WorkerMessage
            {
                MessageType = "Cancel",
                TaskId = taskId
            }, sendCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "发送 Worker 取消消息失败，直接终止进程 {TaskId}", taskId);
        }

        try
        {
            await worker.CompletionTask.WaitAsync(_cancelGracePeriod);
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("Worker {TaskId} 未在宽限期内响应取消，强制终止", taskId);
            worker.SetCancelled();
            CleanupWorker(worker);
        }

        // 回执先于清理：取消返回时必须已结束进程并移出运行表
        try { await worker.CleanupCompleted.WaitAsync(_cancelGracePeriod); }
        catch (TimeoutException) { }
    }

    public Task ForceTerminateWorkerAsync(Guid taskId)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            try { worker.Process.Kill(entireProcessTree: true); } catch { }
            CleanupWorker(worker);
        }
        return Task.CompletedTask;
    }

    public async Task<WorkerResult> WaitForCompletionAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            var result = await worker.CompletionTask.WaitAsync(cancellationToken);
            // 终态回执由监听线程先置结果、再在 finally 清理进程；调用方拿到结果时应已可再次启动同任务。
            try { await worker.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (TimeoutException) { }
            return result;
        }

        if (_completions.TryGetValue(taskId, out var completion))
        {
            return await completion.WaitAsync(cancellationToken);
        }

        throw new InvalidOperationException($"Worker {taskId} not found");
    }

    /// <summary>应用退出时终止全部 Worker，避免留下孤儿进程。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var worker in _runningWorkers.Values)
        {
            worker.ListenerCts.Cancel();
            KillProcess(worker.Process);
            worker.Dispose();
        }
        _runningWorkers.Clear();
        _completions.Clear();
    }

    private static void KillProcess(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task CaptureStandardErrorAsync(Process process, StringBuilder buffer)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (buffer)
                {
                    buffer.AppendLine(line);
                    // 只保留尾部的诊断上下文，避免崩溃循环把内存撑大
                    if (buffer.Length > 8000) buffer.Remove(0, buffer.Length - 4000);
                }
            }
        }
        catch
        {
            // 进程被强杀或管道关闭时读取会中断，诊断信息已部分保留
        }
    }
}

/// <summary>
/// Represents a running worker process
/// </summary>
internal class WorkerProcess : IDisposable
{
    private readonly TaskCompletionSource<WorkerResult> _completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid TaskId { get; }
    public Process Process { get; }
    public NamedPipeServer Pipe { get; }
    public IProgress<WorkerTaskProgress>? Progress { get; }
    public WorkerPromptHandler? PromptHandler { get; }
    public Task StandardErrorCapture { get; }

    private readonly StringBuilder _standardError;

    /// <summary>崩溃诊断：尾部 stderr 追加到错误消息。</summary>
    public string StandardErrorTail
    {
        get
        {
            lock (_standardError)
            {
                return _standardError.Length == 0 ? string.Empty : Environment.NewLine + _standardError;
            }
        }
    }

    /// <summary>监听循环的取消源，与工作者同生命周期，不随启动调用方令牌取消。</summary>
    public CancellationTokenSource ListenerCts { get; } = new();

    private readonly TaskCompletionSource _cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>进程已结束且已移出运行表；取消后返回前必须等待它。</summary>
    public Task CleanupCompleted => _cleaned.Task;

    public void MarkCleaned() => _cleaned.TrySetResult();

    public Task<WorkerResult> CompletionTask => _completionSource.Task;

    public WorkerProcess(Guid taskId, Process process, NamedPipeServer pipe,
        IProgress<WorkerTaskProgress>? progress, WorkerPromptHandler? promptHandler,
        StringBuilder standardError, Task standardErrorCapture)
    {
        TaskId = taskId;
        Process = process;
        Pipe = pipe;
        Progress = progress;
        PromptHandler = promptHandler;
        _standardError = standardError;
        StandardErrorCapture = standardErrorCapture;
    }

    public void SetResult(bool success, string? result, List<string>? errors, bool partiallyFailed,
        string? data = null)
    {
        _completionSource.TrySetResult(new WorkerResult
        {
            Success = success,
            PartiallyFailed = partiallyFailed,
            Result = result,
            Data = data,
            Errors = errors ?? new List<string>(),
            Crashed = false
        });
    }

    public void SetError(string errorMessage)
    {
        _completionSource.TrySetResult(new WorkerResult
        {
            Success = false,
            Result = null,
            Errors = new List<string> { errorMessage },
            Crashed = false
        });
    }

    public void SetCancelled()
    {
        _completionSource.TrySetResult(new WorkerResult
        {
            Success = false,
            Cancelled = true,
            Crashed = false
        });
    }

    public void SetCrashed(string errorMessage)
    {
        _completionSource.TrySetResult(new WorkerResult
        {
            Success = false,
            Result = null,
            Errors = new List<string> { $"Worker process crashed: {errorMessage}" },
            Crashed = true
        });
    }

    public void KillAndDispose()
    {
        ListenerCts.Cancel();
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        Pipe.Dispose();
        Process.Dispose();
    }
}
