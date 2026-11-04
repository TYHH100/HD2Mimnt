using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Cli;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// CLI 命令管道宿主：接收 HD2Mimnt.Cli 转发的 JSON 请求，在运行中的 UI 进程内
/// 经 CliCommandCatalog 调用现有服务执行，回写 JSON 响应。
/// 只在主实例（BrowserProtocolHost.IsPrimary）启动；同一时间只处理一条连接，天然串行化 CLI 写操作。
/// </summary>
public sealed class CliCommandPipeHost : IDisposable
{
    private static readonly JsonSerializerOptions PacketOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _pipeName;
    private readonly IServiceProvider _services;
    private readonly CliCommandCatalog _catalog = CliCommandCatalog.Build();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;

    public CliCommandPipeHost(IServiceProvider services)
    {
        _services = services;
        _pipeName = CliProtocol.GetPipeName();
        _listener = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var perConnection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                // 单命令上限 30 分钟：部署/导入可能很慢，但不允许客户端失联后无限挂起。
                perConnection.CancelAfter(TimeSpan.FromMinutes(30));
                var packet = await ReadPacketAsync(server, perConnection.Token).ConfigureAwait(false);
                CliCommandResponse response;
                if (packet == null)
                    response = CliCommandResponse.Usage("请求不是合法的 CLI 包。");
                else
                    response = await DispatchAsync(packet, perConnection.Token).ConfigureAwait(false);
                await WriteResultAsync(server, ToResult(response), perConnection.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                or DecoderFallbackException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                    await Task.Delay(100, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<CliPipePacket?> ReadPacketAsync(NamedPipeServerStream server, CancellationToken token)
    {
        var payload = await ReadFrameAsync(server, CliProtocol.MaxRequestBytes, token).ConfigureAwait(false);
        if (payload == null) return null;
        return JsonSerializer.Deserialize<CliPipePacket>(payload, PacketOptions);
    }

    private async Task WriteResultAsync(NamedPipeServerStream server, CliPipeResult result, CancellationToken token)    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, ResultOptions);
        if (payload.Length > CliProtocol.MaxResponseBytes)
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(1, "响应超出管道上限，请缩小查询范围（如用 --limit）。", null, false), ResultOptions);
        }
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        await server.WriteAsync(size, token).ConfigureAwait(false);
        await server.WriteAsync(payload, token).ConfigureAwait(false);
        await server.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<CliCommandResponse> DispatchAsync(CliPipePacket packet, CancellationToken token)
    {
        try
        {
            return await _catalog.DispatchAsync(packet.Tokens, _services, packet.Json, packet.Yes, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return CliCommandResponse.Fail("命令执行超时（30 分钟上限），已中止。");
        }
        catch (Exception ex)
        {
            return CliCommandResponse.Fail("命令执行出现未处理异常：" + ex.Message);
        }
    }

    private static CliPipeResult ToResult(CliCommandResponse response)
        => new(response.ExitCode, response.Message, response.Data, response.RequiresYes);

    /// <summary>读一帧：4 字节小端长度 + 载荷；超限或断流返回 null。</summary>
    private static async Task<byte[]?> ReadFrameAsync(NamedPipeServerStream server, int maxBytes, CancellationToken token)
    {
        var size = new byte[4];
        await server.ReadExactlyAsync(size, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length <= 0 || length > maxBytes) return null;
        var payload = new byte[length];
        await server.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        catch { /* 监听循环的收尾异常不影响退出。 */ }
        _stop.Dispose();
    }
}
