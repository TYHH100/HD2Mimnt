using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Cli;
using HD2Mimnt.Core.IPC;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Agent;

/// <summary>
/// Agent 管道宿主：UI 未运行时常驻服务端。单实例串行处理命令（与 UI 的 CliCommandPipeHost 同模式），
/// 支持流式 progress 帧、prompt 双向帧与 shutdown 控制帧；空闲超时或 UI 让位请求后自动退出。
/// </summary>
public sealed class AgentPipeServer : IDisposable
{
    private static readonly JsonSerializerOptions PacketOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ServiceProvider _services;
    private readonly CliCommandCatalog _catalog = CliCommandCatalog.Build();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CliPipePromptResponse>> _pendingPrompts = new();
    private readonly long _idleTicks;
    private readonly Task _listener;
    private long _lastActivityTicks = Environment.TickCount64;
    private volatile bool _commandInProgress;

    public AgentPipeServer(ServiceProvider services)
    {
        _services = services;
        // HD2MIMNT_AGENT_IDLE_SECONDS：空闲自动退出秒数；0 或负数表示常驻不退出。默认 300。
        var idleSeconds = long.TryParse(Environment.GetEnvironmentVariable("HD2MIMNT_AGENT_IDLE_SECONDS"), out var parsed)
            ? parsed
            : 300;
        _idleTicks = idleSeconds > 0 ? idleSeconds * TimeSpan.TicksPerSecond : 0;
        _listener = ListenAsync();
    }

    /// <summary>阻塞直到收到 shutdown / 空闲退出 / 停止信号；返回 0 表示正常退出。</summary>
    public async Task<int> WaitForExitAsync()
    {
        await _shutdown.Task.ConfigureAwait(false);
        await _listener.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        // 正在执行的命令已在监听循环内等待完成；关闭容器归还数据库句柄。
        await _services.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>请求退出：停止接受新连接，监听循环在当前命令完成后收尾。</summary>
    public void RequestShutdown(string reason)
    {
        AgentLog.Write("Agent 退出：" + reason);
        _shutdown.TrySetResult();
        _stop.Cancel();
    }

    private async Task ListenAsync()
    {
        // UI 已在运行则立即让位：客户端探测 UI 管道在先，这里只是竞态兜底。
        if (PipeProbe.Exists(CliPipeProtocol.GetUiPipeName()))
        {
            RequestShutdown("UI 已在运行");
            return;
        }

        var idleMonitor = MonitorIdleAsync();
        try
        {
            while (!_stop.IsCancellationRequested && !_shutdown.Task.IsCompleted)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(CliPipeProtocol.GetAgentPipeName(), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
                    using var perConnection = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    // 单命令上限 30 分钟：部署/导入可能很慢，但不允许客户端失联后无限挂起。
                    perConnection.CancelAfter(TimeSpan.FromMinutes(30));
                    await HandleConnectionAsync(server, perConnection.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                    or DecoderFallbackException or ObjectDisposedException)
                {
                    if (!_stop.IsCancellationRequested && !_shutdown.Task.IsCompleted)
                        await System.Threading.Tasks.Task.Delay(100, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                catch (Exception ex)
                {
                    // 未知异常不能让监听 Task 静默死亡（daemon 会表现为"管道消失"无任何日志）。
                    AgentLog.Write("监听循环出现未处理异常，重建监听实例", ex);
                    if (!_stop.IsCancellationRequested && !_shutdown.Task.IsCompleted)
                        await System.Threading.Tasks.Task.Delay(100, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            _shutdown.TrySetResult();
            await idleMonitor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task MonitorIdleAsync()
    {
        while (!_stop.IsCancellationRequested && !_shutdown.Task.IsCompleted)
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10), _stop.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_idleTicks == 0 || _commandInProgress || _shutdown.Task.IsCompleted) continue;
            if (Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks) > _idleTicks / TimeSpan.TicksPerMillisecond)
            {
                RequestShutdown($"空闲超限（{_idleTicks / TimeSpan.TicksPerSecond} 秒无命令）");
            }
        }
    }

    /// <summary>处理一条连接：控制帧 / prompt 应答帧 / 命令请求帧。</summary>
    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        var frame = await ReadFrameAsync(server, CliPipeProtocol.MaxRequestBytes, token).ConfigureAwait(false);
        if (frame == null) return;

        JsonElement root;
        try
        {
            // Clone 让 JsonElement 脱离 JsonDocument 的池化内存：下游异步处理期间 document 可能已被回收。
            using var document = JsonDocument.Parse(frame);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            AgentLog.Write("收到非法 JSON 帧：" + ex.Message);
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(2, "请求不是合法的 JSON 帧。", null, false), ResultOptions), token).ConfigureAwait(false);
            return;
        }

        var frameType = root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        switch (frameType)
        {
            case CliPipeProtocol.FrameControl:
                await HandleControlAsync(server, root, token).ConfigureAwait(false);
                return;
            case "prompt-response":
                HandlePromptResponse(root);
                return;
            default:
                await HandleCommandAsync(server, root, token).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleControlAsync(NamedPipeServerStream server, JsonElement root, CancellationToken token)
    {
        var control = JsonSerializer.Deserialize<CliPipeControl>(root.GetRawText(), PacketOptions);
        var command = control?.Command?.ToLowerInvariant();
        if (command == "shutdown")
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(0, "Agent 已收到退出请求。", null, false), ResultOptions), token).ConfigureAwait(false);
            RequestShutdown("收到 shutdown 控制帧");
        }
        else if (command == "status")
        {
            var uptime = TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(new CliPipeResult(0,
                $"Agent 运行中（PID {Environment.ProcessId}，已运行 {uptime:hh\\:mm\\:ss}）。", null, false), ResultOptions), token).ConfigureAwait(false);
        }
        else
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(2, $"未知控制命令：{command ?? "(空)"}（支持 shutdown/status）。", null, false), ResultOptions), token).ConfigureAwait(false);
        }
    }

    private void HandlePromptResponse(JsonElement root)
    {
        var response = JsonSerializer.Deserialize<CliPipePromptResponse>(root.GetRawText(), PacketOptions);
        if (response == null) return;
        if (_pendingPrompts.TryRemove(response.Id, out var pending)) pending.TrySetResult(response);
    }

    private async Task HandleCommandAsync(NamedPipeServerStream server, JsonElement root, CancellationToken token)
    {
        var packet = JsonSerializer.Deserialize<CliPipePacket>(root.GetRawText(), PacketOptions);
        if (packet == null || packet.Tokens.Count == 0)
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(2, "请求不是合法的 CLI 包。", null, false), ResultOptions), token).ConfigureAwait(false);
            return;
        }

        _commandInProgress = true;
        try
        {
            CliCommandResponse response;
            if (packet.Stream)
            {
                var session = new CliCommandSession
                {
                    Progress = new FrameProgressReporter(server, token),
                    PromptHandler = (prompt, ct) => AskPromptAsync(server, prompt, ct)
                };
                response = await _catalog.DispatchAsync(packet.Tokens, _services, packet.Json, packet.Yes, token, session, packet.Async)
                    .ConfigureAwait(false);
            }
            else
            {
                response = await _catalog.DispatchAsync(packet.Tokens, _services, packet.Json, packet.Yes, token, null, packet.Async)
                    .ConfigureAwait(false);
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(ToResult(response), ResultOptions);
            if (payload.Length > CliPipeProtocol.MaxResponseBytes)
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(
                    new CliPipeResult(1, "响应超出管道上限，请缩小查询范围（如用 --limit）。", null, false), ResultOptions);
            }
            await WriteFrameAsync(server, payload, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(1, "命令执行超时（30 分钟上限），已中止。", null, false), ResultOptions), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                new CliPipeResult(1, "命令执行出现未处理异常：" + ex.Message, null, false), ResultOptions), token).ConfigureAwait(false);
        }
        finally
        {
            _commandInProgress = false;
            Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        }
    }

    /// <summary>把 prompt 帧发给客户端并等待应答；连接断开或超时返回 null（业务按取消处理）。</summary>
    private async Task<CliPipePromptResponse?> AskPromptAsync(NamedPipeServerStream server, CliPipePrompt prompt, CancellationToken token)
    {
        var pending = new TaskCompletionSource<CliPipePromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPrompts[prompt.Id] = pending;
        try
        {
            await WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(prompt, ResultOptions), token).ConfigureAwait(false);
            return await pending.Task.WaitAsync(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pendingPrompts.TryRemove(prompt.Id, out _);
        }
    }

    private static CliPipeResult ToResult(CliCommandResponse response)
        => new(response.ExitCode, response.Message, response.Data, response.RequiresYes);

    /// <summary>流式进度帧写出：同步回调直发，避免 Progress.Post 的线程池乱序。</summary>
    private sealed class FrameProgressReporter(NamedPipeServerStream server, CancellationToken token) : IProgress<CliProgressEvent>
    {
        public void Report(CliProgressEvent value)
        {
            try
            {
                WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(
                    new CliPipeProgress(value.Stage, value.Percent, value.Current, value.Message ?? ""), ResultOptions), token)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // 客户端断开时进度写失败不应中断命令本身。
            }
        }
    }

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

    private static async Task WriteFrameAsync(NamedPipeServerStream server, byte[] payload, CancellationToken token)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        await server.WriteAsync(size, token).ConfigureAwait(false);
        await server.WriteAsync(payload, token).ConfigureAwait(false);
        await server.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        catch { /* 监听循环的收尾异常不影响退出。 */ }
        _stop.Dispose();
    }
}
