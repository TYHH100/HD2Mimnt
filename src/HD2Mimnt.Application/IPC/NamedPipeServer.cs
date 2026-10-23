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
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
        const int maxMessageBytes = 8 * 1024 * 1024;

        var lengthBuffer = new byte[4];
        if (!await ReadExactlyAsync(lengthBuffer, cancellationToken)) return null;

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length is <= 0 or > maxMessageBytes)
        {
            // 长度非法：视为协议破坏，抛出让监听器按工作者异常处理
            throw new IOException($"Invalid IPC message length: {length}");
        }

        var buffer = new byte[length];
        if (!await ReadExactlyAsync(buffer, cancellationToken)) return null;

        var json = Encoding.UTF8.GetString(buffer);
        return JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipeServer.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false; // 对端关闭
            offset += read;
        }
        return true;
    }

    public bool IsConnected => _pipeServer.IsConnected;

    public void Dispose()
    {
        _pipeServer?.Dispose();
    }
}
