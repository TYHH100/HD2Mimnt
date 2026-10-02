using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.PluginHost.IPC;

/// <summary>
/// Named pipe client for plugin host IPC
/// </summary>
public class PluginHostPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipeClient;
    private readonly string _pipeName;

    public PluginHostPipeClient(string pipeName)
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
        using var cts = new CancellationTokenSource(timeoutMilliseconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        await _pipeClient.ConnectAsync(linkedCts.Token);
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);

        await _pipeClient.WriteAsync(lengthBytes, 0, 4, cancellationToken);
        await _pipeClient.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await _pipeClient.FlushAsync(cancellationToken);
    }

    public async Task<T?> ReceiveMessageAsync<T>(CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var lengthBytes = new byte[4];
        var bytesRead = await _pipeClient.ReadAsync(lengthBytes, 0, 4, cancellationToken);
        if (bytesRead == 0) return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        var messageBytes = new byte[length];
        bytesRead = await _pipeClient.ReadAsync(messageBytes, 0, length, cancellationToken);
        if (bytesRead == 0) return null;

        var json = Encoding.UTF8.GetString(messageBytes);
        return JsonSerializer.Deserialize<T>(json);
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
