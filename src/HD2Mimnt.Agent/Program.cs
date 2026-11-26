namespace HD2Mimnt.Agent;

/// <summary>
/// HD2Mimnt.Agent：CLI 的托管服务端。serve 模式常驻（UI 未运行时的守护进程），
/// run-once 模式一次性执行命令（daemon 拉起失败时的兜底，与 GUI 共享同一套业务服务）。
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: HD2Mimnt.Agent serve | run-once [--json] [--yes] [--stream] [--timeout 秒] -- <命令 tokens...>");
            return 2;
        }

        try
        {
            if (args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
                return await RunServeAsync();

            if (args[0].Equals("run-once", StringComparison.OrdinalIgnoreCase))
                return await ExecuteRunOnceAsync(args.Skip(1).ToArray());

            Console.Error.WriteLine($"未知模式：{args[0]}。支持 serve 与 run-once。");
            return 2;
        }
        catch (Exception ex)
        {
            AgentLog.Write("Agent 异常退出", ex);
            Console.Error.WriteLine("Agent 异常退出：" + ex.Message);
            return 1;
        }
    }

    /// <summary>serve：构建容器 → 常驻监听，直到 shutdown / 空闲退出。</summary>
    private static async Task<int> RunServeAsync()
    {
        AgentLog.Write($"Agent serve 启动（数据目录探测中）");
        var provider = await AgentHost.BuildAsync(CancellationToken.None);
        AgentLog.Write("Agent 容器就绪，开始监听");
        using var server = new AgentPipeServer(provider);
        return await server.WaitForExitAsync();
    }

    /// <summary>run-once：解析全局选项与 "--" 后的命令 token，容器内执行单条命令。</summary>
    private static async Task<int> ExecuteRunOnceAsync(string[] args)
    {
        var json = false;
        var yes = false;
        var stream = false;
        var async = false;
        int? timeoutSeconds = null;
        var tokens = new List<string>();
        var pastSeparator = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (pastSeparator) { tokens.Add(arg); continue; }
            if (arg == "--") { pastSeparator = true; continue; }
            if (arg == "--json") { json = true; continue; }
            if (arg == "--yes" || arg == "-y") { yes = true; continue; }
            if (arg == "--stream") { stream = true; continue; }
            if (arg == "--async") { async = true; continue; }
            if (arg == "--timeout" || arg.StartsWith("--timeout=", StringComparison.Ordinal))
            {
                var value = arg.StartsWith("--timeout=", StringComparison.Ordinal) ? arg["--timeout=".Length..]
                    : index + 1 < args.Length ? args[++index] : null;
                if (!int.TryParse(value, out var parsed) || parsed <= 0)
                    throw new ArgumentException("--timeout 需要正整数秒数。");
                timeoutSeconds = parsed;
                continue;
            }
            tokens.Add(arg);
        }

        if (tokens.Count == 0)
        {
            Console.Error.WriteLine("run-once 需要 -- 之后的命令 token，例如: run-once -- mod list");
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds ?? 600));
        return await AgentHost.RunOnceAsync(tokens, json, yes, stream, async, TimeSpan.FromSeconds(timeoutSeconds ?? 600), cts.Token);
    }
}
