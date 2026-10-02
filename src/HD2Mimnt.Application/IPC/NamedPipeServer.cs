using System.IO.Pipes;
using System.Text;
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
            PipeOptions.Asynchronous);
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _pipeServer.WaitForConnectionAsync(cancellationToken);
    }

    public async Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(bytes.Length);

        await _pipeServer.WriteAsync(length, cancellationToken);
        await _pipeServer.WriteAsync(bytes, cancellationToken);
        await _pipeServer.FlushAsync(cancellationToken);
    }

    public async Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[4];
        var bytesRead = await _pipeServer.ReadAsync(lengthBuffer, cancellationToken);
        if (bytesRead == 0) return null;

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        var buffer = new byte[length];
        bytesRead = await _pipeServer.ReadAsync(buffer, cancellationToken);

        if (bytesRead == 0) return null;

        var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        return JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    public bool IsConnected => _pipeServer.IsConnected;

    public void Dispose()
    {
        _pipeServer?.Dispose();
    }
}
