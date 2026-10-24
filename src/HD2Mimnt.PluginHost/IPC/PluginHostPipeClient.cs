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

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : PluginHostMessage
        => PipeFrame.SendAsync(_pipeClient, JsonSerializer.Serialize(message), cancellationToken);

    public async Task<T?> ReceiveMessageAsync<T>(CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var json = await PipeFrame.ReceiveAsync(_pipeClient, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>读取一帧原始 JSON；返回 null 表示对端关闭。</summary>
    public Task<string?> ReceiveRawAsync(CancellationToken cancellationToken = default)
        => PipeFrame.ReceiveAsync(_pipeClient, cancellationToken);

    public bool IsConnected => _pipeClient.IsConnected;

    public void Dispose()
    {
        _pipeClient?.Dispose();
    }
}
