using System.Diagnostics;
using System.Text.Json;
using HD2Mimnt.Application.IPC;
using HD2Mimnt.Application.Workers;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Manages worker processes and IPC communication
/// </summary>
public class WorkerHostManager : IWorkerHostManager
{
    private readonly ITaskRepository _taskRepository;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WorkerProcess> _runningWorkers = new();

    public WorkerHostManager(ITaskRepository taskRepository)
    {
        _taskRepository = taskRepository;
    }

    public async Task<Guid> StartWorkerAsync(
        string workerType,
        Dictionary<string, string> arguments,
        IProgress<WorkerTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid();
        var pipeName = $"HD2Mimnt.Worker.{taskId}";

        // Create named pipe server
        var pipeServer = new NamedPipeServer(pipeName);

        // Spawn worker process
        var workerExePath = GetWorkerExecutablePath();
        var processStartInfo = new ProcessStartInfo
        {
            FileName = workerExePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("--task-id");
        processStartInfo.ArgumentList.Add(taskId.ToString());
        processStartInfo.ArgumentList.Add("--worker");
        processStartInfo.ArgumentList.Add(workerType);
        processStartInfo.ArgumentList.Add("--pipe-name");
        processStartInfo.ArgumentList.Add(pipeName);

        var process = Process.Start(processStartInfo);
        if (process == null)
        {
            pipeServer.Dispose();
            throw new InvalidOperationException("Failed to start worker process");
        }

        // 连接等待：外部取消与超时分开判定，失败路径统一清理进程与管道
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipeServer.WaitForConnectionAsync(connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            pipeServer.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Worker process failed to connect within timeout");
        }

        // Send execute command
        var command = new ExecuteWorkerCommand
        {
            WorkerType = workerType,
            Arguments = arguments
        };

        var commandMessage = new WorkerMessage
        {
            MessageType = "Command",
            TaskId = taskId,
            Payload = JsonSerializer.Serialize(command)
        };

        await pipeServer.SendMessageAsync(commandMessage, cancellationToken);

        // Start progress listener
        var workerProcess = new WorkerProcess(taskId, process, pipeServer, progress);
        _runningWorkers[taskId] = workerProcess;

        // 监听器与工作者同生命周期，不随启动调用方令牌取消
        _ = ListenForMessagesAsync(workerProcess, workerProcess.ListenerCts.Token);

        return taskId;
    }

    private async Task ListenForMessagesAsync(WorkerProcess worker, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && worker.Pipe.IsConnected)
            {
                var message = await worker.Pipe.ReceiveMessageAsync(cancellationToken);
                if (message == null) break;

                switch (message.MessageType)
                {
                    case "Progress":
                        var progressData = JsonSerializer.Deserialize<WorkerProgressResponse>(message.Payload ?? "{}");
                        if (progressData != null)
                        {
                            worker.Progress?.Report(new WorkerTaskProgress
                            {
                                CurrentStep = progressData.CurrentStep,
                                TotalSteps = progressData.TotalSteps,
                                Message = progressData.Message
                            });
                        }
                        break;

                    case "Completed":
                        var completedData = JsonSerializer.Deserialize<WorkerCompletedResponse>(message.Payload ?? "{}");
                        if (completedData != null)
                        {
                            worker.SetResult(completedData.Success, completedData.Result, completedData.Errors);
                        }
                        return;

                    case "Error":
                        var errorData = JsonSerializer.Deserialize<WorkerErrorResponse>(message.Payload ?? "");
                        if (errorData != null)
                        {
                            worker.SetError(errorData.ErrorMessage);
                        }
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消路径：不当作崩溃记录，统一走 finally 清理
        }
        catch (Exception ex)
        {
            // Worker crashed or pipe disconnected
            worker.SetCrashed(ex.Message);
        }
        finally
        {
            CleanupWorker(worker);
        }
    }

    private void CleanupWorker(WorkerProcess worker)
    {
        worker.ListenerCts.Cancel();
        try
        {
            if (!worker.Process.HasExited)
            {
                worker.Process.Kill();
            }
        }
        catch { }

        worker.Dispose();
        _runningWorkers.TryRemove(worker.TaskId, out _);
    }

    public Task<bool> IsWorkerRunningAsync(Guid taskId)
    {
        return Task.FromResult(_runningWorkers.ContainsKey(taskId));
    }

    public async Task CancelWorkerAsync(Guid taskId)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            try
            {
                // Send cancellation signal (worker should handle gracefully)
                worker.Process.Kill();
            }
            catch { }

            CleanupWorker(worker);
        }

        await Task.CompletedTask;
    }

    public async Task ForceTerminateWorkerAsync(Guid taskId)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            try
            {
                worker.Process.Kill(entireProcessTree: true);
            }
            catch { }

            CleanupWorker(worker);
        }

        await Task.CompletedTask;
    }

    public async Task<WorkerResult> WaitForCompletionAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            return await worker.CompletionTask.WaitAsync(cancellationToken);
        }

        throw new InvalidOperationException($"Worker {taskId} not found");
    }

    private static string GetWorkerExecutablePath()
    {
        // Worker executable is in the same directory as the main app
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDirectory, "HD2Mimnt.Worker.exe");
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

    /// <summary>监听循环的取消源，与工作者同生命周期，不随启动调用方令牌取消。</summary>
    public System.Threading.CancellationTokenSource ListenerCts { get; } = new();

    public Task<WorkerResult> CompletionTask => _completionSource.Task;

    public WorkerProcess(Guid taskId, Process process, NamedPipeServer pipe, IProgress<WorkerTaskProgress>? progress)
    {
        TaskId = taskId;
        Process = process;
        Pipe = pipe;
        Progress = progress;
    }

    public void SetResult(bool success, string? result, List<string>? errors)
    {
        _completionSource.TrySetResult(new WorkerResult
        {
            Success = success,
            Result = result,
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

    public void Dispose()
    {
        Pipe?.Dispose();
        Process?.Dispose();
    }
}
