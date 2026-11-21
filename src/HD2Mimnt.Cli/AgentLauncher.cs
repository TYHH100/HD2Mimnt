using System.Diagnostics;
using System.Text;

namespace HD2Mimnt.Cli;

/// <summary>
/// 服务端可执行文件定位与拉起：Agent（serve/run-once）与 UI（launch）。
/// 优先环境变量，其次同目录（发布布局），最后开发布局回退。
/// </summary>
internal static class AgentLauncher
{
    /// <summary>定位 HD2Mimnt.Agent.exe；找不到返回 null。</summary>
    public static string? Find()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HD2MIMNT_AGENT_EXE");
        if (IsExecutable(fromEnvironment)) return fromEnvironment;

        var baseDirectory = AppContext.BaseDirectory;
        var sameDirectory = Path.Combine(baseDirectory, "HD2Mimnt.Agent.exe");
        if (IsExecutable(sameDirectory)) return sameDirectory;

        // 开发布局：<repo>/src/HD2Mimnt.Cli/bin/Debug/net10.0 → <repo>/src/HD2Mimnt.Agent/bin/{Debug,Release}/net10.0[/win-x64]
        if (baseDirectory.Contains(Path.Combine("HD2Mimnt.Cli", "bin"), StringComparison.OrdinalIgnoreCase))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                // RID 项目（Agent 与 UI）输出在 net10.0/win-x64 子目录；无 RID 布局留 net10.0 兜底。
                var withRid = Path.Combine(repoRoot, "src", "HD2Mimnt.Agent", "bin", configuration, "net10.0", "win-x64", "HD2Mimnt.Agent.exe");
                if (IsExecutable(withRid)) return withRid;
                var candidate = Path.Combine(repoRoot, "src", "HD2Mimnt.Agent", "bin", configuration, "net10.0", "HD2Mimnt.Agent.exe");
                if (IsExecutable(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>分离式拉起 Agent serve；成功返回 true。daemon 已运行时 serve 自会因管道占用退出，无需先探测。</summary>
    public static bool TryStartServe(string agentExe)
    {
        // 不继承任何句柄：CLI 可能从管道/重定向环境启动，serve 持有写端会让上游永远等不到 EOF。
        return DetachedProcess.Start(agentExe, "serve", Path.GetDirectoryName(agentExe));
    }

    /// <summary>run-once 兜底：子进程继承当前控制台直接输出，阻塞等待并透传退出码。</summary>
    public static async Task<int> RunOnceAsync(string agentExe, CliInvocation invocation, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var arguments = BuildRunOnceArguments(invocation, tokens);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = agentExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return 1;
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine("启动 Agent 兜底执行失败：" + ex.Message);
            return 1;
        }
    }

    internal static string BuildRunOnceArguments(CliInvocation invocation, IReadOnlyList<string> tokens)
    {
        var builder = new StringBuilder("run-once");
        if (invocation.Json) builder.Append(" --json");
        if (invocation.Yes) builder.Append(" --yes");
        if (invocation.Stream) builder.Append(" --stream");
        if (invocation.Async) builder.Append(" --async");
        if (invocation.TimeoutSeconds is { } seconds) builder.Append(" --timeout ").Append(seconds);
        builder.Append(" --");
        foreach (var token in tokens)
            builder.Append(" \"").Append(token.Replace("\"", "\\\"")).Append('"');
        return builder.ToString();
    }

    private static string baseDirectoryFallback() => AppContext.BaseDirectory;

    private static bool IsExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}

/// <summary>launch 命令：定位并启动 HD2Mimnt.UI.exe，等待 CLI 管道就绪（上限 60 秒）。</summary>
internal static class UiLauncher
{
    /// <summary>定位 HD2Mimnt.UI.exe；找不到返回 null。</summary>
    public static string? Find()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HD2MIMNT_UI_EXE");
        if (IsUiExecutable(fromEnvironment)) return fromEnvironment;

        var sameDirectory = Path.Combine(AppContext.BaseDirectory, "HD2Mimnt.UI.exe");
        if (IsUiExecutable(sameDirectory)) return sameDirectory;

        var baseDirectory = AppContext.BaseDirectory;
        if (baseDirectory.Contains(Path.Combine("HD2Mimnt.Cli", "bin"), StringComparison.OrdinalIgnoreCase))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var withRid = Path.Combine(repoRoot, "src", "HD2Mimnt.UI", "bin", configuration, "net10.0", "win-x64", "HD2Mimnt.UI.exe");
                if (IsUiExecutable(withRid)) return withRid;
                var candidate = Path.Combine(repoRoot, "src", "HD2Mimnt.UI", "bin", configuration, "net10.0", "HD2Mimnt.UI.exe");
                if (IsUiExecutable(candidate)) return candidate;
            }
        }
        return null;
    }

    public static bool TryStart(string uiExe)
    {
        // 与 serve 同理：不继承句柄，避免 UI 长进程占住 CLI 的控制台管道。
        return DetachedProcess.Start(uiExe, string.Empty, Path.GetDirectoryName(uiExe));
    }

    private static bool IsUiExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && Path.GetFileName(path).Equals("HD2Mimnt.UI.exe", StringComparison.OrdinalIgnoreCase);
}
