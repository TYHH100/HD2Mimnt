using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker.IPC;

/// <summary>
/// Named pipe client for worker process to communicate with main app
/// </summary>
public class NamedPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipeClient;
    private readonly string _pipeName;

    public NamedPipeClient(string pipeName)
    {
        _pipeName = pipeName;
        _pipeClient = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default)
    {
        await _pipeClient.ConnectAsync(timeoutMilliseconds, cancellationToken);
    }

    public async Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(bytes.Length);

        await _pipeClient.WriteAsync(length, cancellationToken);
        await _pipeClient.WriteAsync(bytes, cancellationToken);
        await _pipeClient.FlushAsync(cancellationToken);
    }

    public async Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[4];
        var bytesRead = await _pipeClient.ReadAsync(lengthBuffer, cancellationToken);
        if (bytesRead == 0) return null;

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        var buffer = new byte[length];
        bytesRead = await _pipeClient.ReadAsync(buffer, cancellationToken);

        if (bytesRead == 0) return null;

        var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        return JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
