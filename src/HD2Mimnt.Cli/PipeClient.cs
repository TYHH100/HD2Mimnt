using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Cli;

/// <summary>AOT 客户端的 JSON 源生成上下文：只覆盖客户端会构造/解析的固定帧类型。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliPipePacket))]
[JsonSerializable(typeof(CliPipeControl))]
[JsonSerializable(typeof(CliPipePromptResponse))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

/// <summary>
/// CLI 薄客户端的管道收发：存在性探测（亚毫秒）→ ConnectAsync（短超时）→ 帧收发循环。
/// 流式模式下逐帧处理 progress/prompt，直到 result 帧返回退出码。
/// </summary>
internal static class PipeClient
{
    private static readonly TimeSpan ConnectProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>连接指定管道；探测确认存在后才 ConnectAsync，失败返回 null。</summary>
    public static async Task<NamedPipeClientStream?> ConnectAsync(string pipeName)
    {
        if (!PipeProbe.Exists(pipeName)) return null;
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var cts = new CancellationTokenSource(ConnectProbeTimeout);
            await client.ConnectAsync(cts.Token);
            return client;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            await client.DisposeAsync();
            return null;
        }
    }

    /// <summary>发送命令请求并处理响应帧流，返回命令退出码。</summary>
    public static async Task<int> ExecuteAsync(NamedPipeClientStream client, CliPipePacket packet, bool json, TimeSpan timeout)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, CliJsonContext.Default.CliPipePacket);
        if (payload.Length > CliPipeProtocol.MaxRequestBytes)
            return PrintUsage("请求超出管道上限，请缩小参数规模。");

        using var timeoutCts = new CancellationTokenSource(timeout);
        var token = timeoutCts.Token;
        try
        {
            await WriteFrameAsync(client, payload, token);
            while (true)
            {
                var frame = await ReadFrameAsync(client, token);
                if (frame == null)
                {
                    Console.Error.WriteLine("与运行中的服务端通信中断，命令可能已执行。请先确认结果，避免重复执行破坏性命令；也可关闭管理器后重试。");
                    return 1;
                }
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                var frameType = root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : CliPipeProtocol.FrameResult;

                switch (frameType)
                {
                    case CliPipeProtocol.FrameProgress:
                        PrintProgress(root, json);
                        continue;
                    case CliPipeProtocol.FramePrompt:
                        // 客户端当前版本不承载交互 UI：回一个取消应答，业务按取消处理。
                        if (root.TryGetProperty("id", out var id))
                        {
                            var response = JsonSerializer.SerializeToUtf8Bytes(
                                new CliPipePromptResponse(id.GetString() ?? "", null, Confirmed: false, Cancelled: true),
                                CliJsonContext.Default.CliPipePromptResponse);
                            await WriteFrameAsync(client, response, token);
                        }
                        continue;
                    default:
                        return PrintResult(root, json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"等待响应超时（{timeout.TotalSeconds:F0} 秒），命令可能仍在执行。可用 --timeout 加大等待时间。");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine("与运行中的服务端通信中断，命令可能已执行。请先确认结果，避免重复执行破坏性命令。");
            return 1;
        }
    }

    /// <summary>向服务端发送控制帧（shutdown/status），返回结果退出码。</summary>
    public static async Task<int> SendControlAsync(NamedPipeClientStream client, CliPipeControl control, bool json, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(control, CliJsonContext.Default.CliPipeControl);
            await WriteFrameAsync(client, payload, timeoutCts.Token);
            var frame = await ReadFrameAsync(client, timeoutCts.Token);
            if (frame == null) return 1;
            using var document = JsonDocument.Parse(frame);
            return PrintResult(document.RootElement, json);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Console.Error.WriteLine("控制命令执行失败：" + ex.Message);
            return 1;
        }
    }

    private static int PrintResult(JsonElement root, bool json)
    {
        var exitCode = root.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var parsed) ? parsed : 1;
        var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
        if (json)
        {
            Console.WriteLine(root.GetRawText());
        }
        else if (!string.IsNullOrEmpty(message))
        {
            if (exitCode == 2) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
        }
        return exitCode;
    }

    private static void PrintProgress(JsonElement root, bool json)
    {
        if (json)
        {
            Console.WriteLine(root.GetRawText());
            return;
        }
        var stage = root.TryGetProperty("stage", out var s) ? s.GetString() : null;
        var current = root.TryGetProperty("current", out var c) ? c.GetString() : null;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        double? percent = root.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
        Console.Error.WriteLine($"[{stage ?? "进度"}]{(percent is { } value ? $" {value:P0}" : "")} {current ?? message ?? ""}");
    }

    private static int PrintUsage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static async Task WriteFrameAsync(NamedPipeClientStream client, byte[] payload, CancellationToken token)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        await client.WriteAsync(size, token);
        await client.WriteAsync(payload, token);
        await client.FlushAsync(token);
    }

    private static async Task<byte[]?> ReadFrameAsync(NamedPipeClientStream client, CancellationToken token)
    {
        var size = new byte[4];
        await client.ReadExactlyAsync(size, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length <= 0 || length > CliPipeProtocol.MaxResponseBytes) return null;
        var payload = new byte[length];
        await client.ReadExactlyAsync(payload, token);
        return payload;
    }
}
