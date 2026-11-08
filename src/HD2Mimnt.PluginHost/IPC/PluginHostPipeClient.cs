using System.IO.Pipes;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.PluginHost.IPC;

/// <summary>
/// Named pipe client for plugin host IPC
/// </summary>
public class PluginHostPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipeClient;
    private readonly string _pipeName;

    // 帧为“长度 + 载荷”两次写入，并发发送会交错破坏协议；
    // 日志、命令结果、AI 请求都可能来自不同线程，必须串行化。
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);

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
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await PipeFrame.SendAsync(_pipeClient, JsonSerializer.Serialize(message), cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<T?> ReceiveMessageAsync<T>(CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var json = await ReceiveRawAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>读取一帧原始 JSON；返回 null 表示对端关闭。</summary>
    public async Task<string?> ReceiveRawAsync(CancellationToken cancellationToken = default)
    {
        await _receiveGate.WaitAsync(cancellationToken);
        try
        {
            return await PipeFrame.ReceiveAsync(_pipeClient, cancellationToken);
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
