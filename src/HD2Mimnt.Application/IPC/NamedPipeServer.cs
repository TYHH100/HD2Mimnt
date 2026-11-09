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
    // 监听线程回复 PromptResponse 与取消/启动发送可能并发，PipeFrame 写帧不能交错。
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public NamedPipeServer(string pipeName)
    {
        _pipeName = pipeName;
        // 用字节模式：PipeFrame 已自带长度前缀分帧；消息模式在对端不读管道时
        // 会让 WriteAsync 永久阻塞（挂起的 Worker 正是这种情况），取消消息就发不出去。
        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _pipeServer.WaitForConnectionAsync(cancellationToken);
    }

    public async Task SendMessageAsync(WorkerMessage message, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try { await PipeFrame.SendAsync(_pipeServer, JsonSerializer.Serialize(message), cancellationToken); }
        finally { _sendGate.Release(); }
    }

    public async Task<WorkerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var json = await PipeFrame.ReceiveAsync(_pipeServer, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<WorkerMessage>(json);
    }

    public bool IsConnected => _pipeServer.IsConnected;

    public void Dispose()
    {
        _pipeServer?.Dispose();
        _sendGate.Dispose();
    }
}
