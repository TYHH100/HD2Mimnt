using System.IO.Pipes;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker.IPC;

/// <summary>
/// Worker 进程侧管道客户端；帧读写复用 Core 的 PipeFrame（短读续读 + 8MB 上限）。
/// </summary>
public class NamedPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipeClient;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public NamedPipeClient(string pipeName)
    {
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

    /// <summary>命令接收循环与业务进度上报会并发写同一管道，发送必须串行。</summary>
    public async Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await PipeFrame.SendAsync(_pipeClient, JsonSerializer.Serialize(message), cancellationToken);
        }
        finally { _sendGate.Release(); }
    }

    public async Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var json = await PipeFrame.ReceiveAsync(_pipeClient, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient.Dispose();
        _sendGate.Dispose();
    }
}