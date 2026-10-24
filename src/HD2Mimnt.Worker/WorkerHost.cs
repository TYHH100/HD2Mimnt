using System.Text.Json;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Worker.IPC;

namespace HD2Mimnt.Worker;

/// <summary>
/// Host environment for worker execution, provides IPC communication
/// </summary>
public class WorkerHost
{
    private readonly Guid _taskId;
    private readonly NamedPipeClient _pipeClient;

    public Guid TaskId => _taskId;

    public WorkerHost(Guid taskId, string pipeName)
    {
        _taskId = taskId;
        _pipeClient = new NamedPipeClient(pipeName);
    }

    /// <summary>
    /// Connect to the main app via named pipe
    /// </summary>
    public async Task ConnectAsync(int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default)
    {
        await _pipeClient.ConnectAsync(timeoutMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Receive the execute command from main app
    /// </summary>
    public async Task<ExecuteWorkerCommand?> ReceiveCommandAsync(CancellationToken cancellationToken = default)
    {
        var message = await _pipeClient.ReceiveMessageAsync(cancellationToken);
        if (message == null || message.MessageType != "Command")
        {
            return null;
        }

        return JsonSerializer.Deserialize<ExecuteWorkerCommand>(message.Payload ?? "{}");
    }

    /// <summary>
    /// Send progress update to main app
    /// </summary>
    public async Task SendProgressAsync(int currentStep, int totalSteps, string? message = null, CancellationToken cancellationToken = default)
    {
        var progress = new WorkerProgressResponse
        {
            CurrentStep = currentStep,
            TotalSteps = totalSteps,
            Message = message
        };

        var workerMessage = new WorkerMessage
        {
            MessageType = "Progress",
            TaskId = _taskId,
            Payload = JsonSerializer.Serialize(progress)
        };

        await _pipeClient.SendMessageAsync(workerMessage, cancellationToken);
    }

    /// <summary>
    /// Send completion response to main app
    /// </summary>
    public async Task SendCompletedAsync(bool success, string? result = null, List<string>? errors = null, CancellationToken cancellationToken = default)
    {
        var completed = new WorkerCompletedResponse
        {
            Success = success,
            Result = result,
            Errors = errors
        };

        var workerMessage = new WorkerMessage
        {
            MessageType = "Completed",
            TaskId = _taskId,
            Payload = JsonSerializer.Serialize(completed)
        };

        await _pipeClient.SendMessageAsync(workerMessage, cancellationToken);
    }

    /// <summary>
    /// Send error response to main app
    /// </summary>
    public async Task SendErrorAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        var error = new WorkerErrorResponse
        {
            ErrorMessage = exception.Message,
            StackTrace = exception.StackTrace
        };

        var workerMessage = new WorkerMessage
        {
            MessageType = "Error",
            TaskId = _taskId,
            Payload = JsonSerializer.Serialize(error)
        };

        await _pipeClient.SendMessageAsync(workerMessage, cancellationToken);
    }

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
