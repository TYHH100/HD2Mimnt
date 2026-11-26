using System.Diagnostics;
using System.Text;
using HD2Mimnt.Core;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Cli;

/// <summary>
/// HD2Mimnt 命令行入口（AOT 薄客户端）：毫秒级启动，命令在三个服务端之一执行——
/// 运行中的 UI（与图形界面同一份内存状态）→ Agent 守护进程（UI 未运行时常驻）→
/// Agent run-once 兜底。本进程只做解析、探测、转发与本地命令，不持有业务状态。
/// </summary>
internal static class Program
{
    private static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan LaunchAgentTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CliInvocation invocation;
        try
        {
            invocation = CliInvocationParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (invocation.VersionRequested)
        {
            Console.WriteLine("HD2Mimnt.Cli " + AppInfo.Version);
            return 0;
        }

        var tokens = invocation.Tokens;
        if (invocation.HelpRequested)
            tokens = tokens.Count > 0 ? new[] { "help" }.Concat(tokens).ToArray() : new[] { "help" };
        if (tokens.Count == 0) tokens = new[] { "help" };

        // 本地命令：launch 与 daemon 不进命令分发。
        if (tokens[0].Equals("launch", StringComparison.OrdinalIgnoreCase))
            return await LaunchAsync(invocation.Json);

        if (tokens[0].Equals("daemon", StringComparison.OrdinalIgnoreCase))
            return await DaemonCommandAsync(tokens, invocation);

        var timeout = TimeSpan.FromSeconds(invocation.TimeoutSeconds ?? 600);
        var packet = new CliPipePacket(tokens, invocation.Json, invocation.Yes, invocation.Stream, invocation.Async);

        // 服务端探测顺序：UI（状态最准）→ Agent。PipeProbe 亚毫秒级，未运行的判定不再付出秒级等待。
        await using var uiClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetUiPipeName());
        if (uiClient != null)
            return await PipeClient.ExecuteAsync(uiClient, packet, invocation.Json, timeout);

        await using var agentClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
        if (agentClient != null)
            return await PipeClient.ExecuteAsync(agentClient, packet, invocation.Json, timeout);

        // 都未运行：自动拉起 Agent 守护进程（首条命令付出一次容器初始化，之后毫秒级）。
        var agentExe = AgentLauncher.Find();
        if (agentExe != null && IsDaemonEnabled() && AgentLauncher.TryStartServe(agentExe))
        {
            var deadline = Environment.TickCount64 + (long)LaunchAgentTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(200);
                await using var started = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
                if (started != null)
                    return await PipeClient.ExecuteAsync(started, packet, invocation.Json, timeout);
            }
            AgentStartupFailedHint();
        }

        // 兜底：run-once 一次性执行（daemon 无法拉起或被 HD2MIMNT_NO_DAEMON 禁用时）。
        if (agentExe != null)
            return await AgentLauncher.RunOnceAsync(agentExe, invocation, tokens,
                new CancellationTokenSource(timeout + LaunchAgentTimeout).Token);

        Console.Error.WriteLine("未找到 HD2Mimnt.Agent.exe。请把 CLI 与主程序放在同一目录，或用环境变量 HD2MIMNT_AGENT_EXE 指定服务端路径。");
        return 1;
    }

    private static bool IsDaemonEnabled()
        => Environment.GetEnvironmentVariable("HD2MIMNT_NO_DAEMON") is not { } value
            || !(value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static void AgentStartupFailedHint()
        => Console.Error.WriteLine("Agent 启动超时，改用一次性进程执行（本次较慢）。");

    /// <summary>launch：启动管理器 UI 并等待 CLI 管道就绪（上限 60 秒）。</summary>
    private static async Task<int> LaunchAsync(bool json)
    {
        await using var probe = await PipeClient.ConnectAsync(CliPipeProtocol.GetUiPipeName());
        if (probe != null) return Print(json, 0, "管理器已在运行，CLI 管道就绪。");

        var uiExe = UiLauncher.Find();
        if (uiExe == null)
            return Print(json, 1, "未找到 HD2Mimnt.UI.exe。请把 CLI 与管理器放在同一目录，或用环境变量 HD2MIMNT_UI_EXE 指定主程序路径。");
        if (!UiLauncher.TryStart(uiExe))
            return Print(json, 1, "启动管理器失败。");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(500, cts.Token);
            await using var client = await PipeClient.ConnectAsync(CliPipeProtocol.GetUiPipeName());
            if (client != null) return Print(json, 0, "管理器已启动，CLI 管道就绪。");
        }
        return Print(json, 1, "管理器启动超时（60 秒内 CLI 管道未就绪）。");
    }

    /// <summary>daemon：status / stop / start。直接与 Agent 控制帧交互，不进命令分发。</summary>
    private static async Task<int> DaemonCommandAsync(IReadOnlyList<string> tokens, CliInvocation invocation)
    {
        var verb = tokens.Count >= 2 ? tokens[1].ToLowerInvariant() : "status";
        var json = invocation.Json;

        if (verb == "status")
        {
            await using var uiClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetUiPipeName());
            if (uiClient != null) return Print(json, 0, "管理器 UI 在运行（CLI 命令转发给 UI，Agent 不需要常驻）。");

            await using var agentClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
            if (agentClient != null)
                return await PipeClient.SendControlAsync(agentClient, new CliPipeControl("status"), json, CommandWait);
            return Print(json, 1, "没有运行中的服务端。下一次命令会自动拉起 Agent（首条命令较慢）。");
        }

        if (verb == "stop")
        {
            await using var agentClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
            if (agentClient == null) return Print(json, 0, "Agent 未在运行。");
            return await PipeClient.SendControlAsync(agentClient, new CliPipeControl("shutdown"), json, CommandWait);
        }

        if (verb == "start")
        {
            await using var existing = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
            if (existing != null) return Print(json, 0, "Agent 已在运行。");
            var uiClient = await PipeClient.ConnectAsync(CliPipeProtocol.GetUiPipeName());
            if (uiClient != null) return Print(json, 0, "管理器 UI 在运行，CLI 命令直接转发给 UI。");

            var agentExe = AgentLauncher.Find();
            if (agentExe == null)
                return Print(json, 1, "未找到 HD2Mimnt.Agent.exe。");
            if (!AgentLauncher.TryStartServe(agentExe))
                return Print(json, 1, "启动 Agent 失败。");

            var deadline = Environment.TickCount64 + (long)LaunchAgentTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(200);
                await using var client = await PipeClient.ConnectAsync(CliPipeProtocol.GetAgentPipeName());
                if (client != null) return Print(json, 0, "Agent 已启动，管道就绪。");
            }
            return Print(json, 1, "Agent 启动超时。");
        }

        Console.Error.WriteLine("用法: daemon status | daemon start | daemon stop");
        return 2;
    }

    private static int Print(bool json, int exitCode, string message)
    {
        if (json)
        {
            Console.WriteLine($"{{\"exitCode\":{exitCode},\"message\":\"{EscapeJson(message)}\",\"data\":null,\"requiresYes\":false}}");
        }
        else
        {
            if (exitCode == 2) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
        }
        return exitCode;
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
