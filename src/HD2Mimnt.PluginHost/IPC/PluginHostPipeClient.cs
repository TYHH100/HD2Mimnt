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
        var json = await ReceiveRawAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>读取一帧原始 JSON；返回 null 表示对端关闭。</summary>
    public async Task<string?> ReceiveRawAsync(CancellationToken cancellationToken = default)
    {
        const int maxMessageBytes = 8 * 1024 * 1024;

        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(lengthBytes, cancellationToken)) return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length is <= 0 or > maxMessageBytes)
        {
            throw new IOException($"Invalid IPC message length: {length}");
        }

        var messageBytes = new byte[length];
        if (!await ReadExactlyAsync(messageBytes, cancellationToken)) return null;
        return Encoding.UTF8.GetString(messageBytes);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipeClient.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false; // 对端关闭
            offset += read;
        }
        return true;
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
