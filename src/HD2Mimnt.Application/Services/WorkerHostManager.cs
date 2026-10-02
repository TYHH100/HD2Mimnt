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
            Arguments = $"--task-id {taskId} --worker {workerType} --pipe-name {pipeName}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(processStartInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start worker process");
        }

        // Wait for connection with timeout
        var connectTask = pipeServer.WaitForConnectionAsync(cancellationToken);
        var timeoutTask = Task.Delay(10000, cancellationToken);
        var completedTask = await Task.WhenAny(connectTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            process.Kill();
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

        _ = Task.Run(async () => await ListenForMessagesAsync(workerProcess, cancellationToken), cancellationToken);

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
            return await worker.CompletionTask;
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
    private readonly TaskCompletionSource<WorkerResult> _completionSource = new();

    public Guid TaskId { get; }
    public Process Process { get; }
    public NamedPipeServer Pipe { get; }
    public IProgress<WorkerTaskProgress>? Progress { get; }

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
