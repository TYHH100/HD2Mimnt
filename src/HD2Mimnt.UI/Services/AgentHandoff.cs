using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// UI 启动早期请求 Agent 守护进程让位：Agent 退出后才继续数据库初始化，
/// 消除两个进程并发写 SQLite 与内存状态分歧的窗口。Agent 未运行时毫秒级返回。
/// </summary>
public static class AgentHandoff
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>探测 Agent 管道并发送 shutdown 控制帧；等待其退出（上限约 3 秒），失败不阻塞 UI 启动。</summary>
    public static async Task RequestAgentExitAsync()
    {
        try
        {
            var pipeName = CliPipeProtocol.GetAgentPipeName();
            if (!PipeProbe.Exists(pipeName)) return;

            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(cts.Token);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new CliPipeControl("shutdown"), Options);
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
            await client.WriteAsync(size, cts.Token);
            await client.WriteAsync(payload, cts.Token);
            await client.FlushAsync(cts.Token);

            // 等 Agent 释放管道（即退出）：轮询存在性，超时继续启动（WAL 下短暂并发可容忍）。
            var deadline = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < deadline && PipeProbe.Exists(pipeName))
                await Task.Delay(50, cts.Token);
        }
        catch
        {
            // Agent 不在运行、已退出或握手失败：不影响 UI 启动。
        }
    }
}
