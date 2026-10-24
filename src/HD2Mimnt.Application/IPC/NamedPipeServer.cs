using System.IO.Pipes;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Application.IPC;

/// <summary>
/// Named pipe server for IPC communication with worker processes
/// </summary>
public class NamedPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipeServer;
    private readonly string _pipeName;

    public NamedPipeServer(string pipeName)
    {
        _pipeName = pipeName;
        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _pipeServer.WaitForConnectionAsync(cancellationToken);
    }

    public Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
        => PipeFrame.SendAsync(_pipeServer, JsonSerializer.Serialize(message), cancellationToken);

    public async Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var json = await PipeFrame.ReceiveAsync(_pipeServer, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    public bool IsConnected => _pipeServer.IsConnected;

    public void Dispose()
    {
        _pipeServer?.Dispose();
    }
}
