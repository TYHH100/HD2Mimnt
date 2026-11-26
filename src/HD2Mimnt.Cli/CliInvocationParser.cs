namespace HD2Mimnt.Cli;

/// <summary>GNU 常规选项解析结果。Tokens 是剥离全局选项后的命令 token（首个为命令动词）。</summary>
internal sealed record CliInvocation(
    IReadOnlyList<string> Tokens,
    bool Json,
    bool Yes,
    bool Stream,
    bool Async,
    int? TimeoutSeconds,
    bool HelpRequested,
    bool VersionRequested);

/// <summary>
/// GNU 常规选项解析器：--flag 值 / --flag=值 双形式、选项可出现在任意位置、
/// 短选项与长选项等价（-h/--help、-y/--yes）、-- 之后全部按字面位置参数处理。
/// 未识别的选项原样透传给命令层（子命令专属选项由 handler 解析）。
/// </summary>
internal static class CliInvocationParser
{
    public static CliInvocation Parse(string[] args)
    {
        var json = false;
        var yes = false;
        var stream = false;
        var async = false;
        var help = false;
        var version = false;
        int? timeoutSeconds = null;
        var tokens = new List<string>();
        var pastSeparator = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (pastSeparator) { tokens.Add(arg); continue; }
            switch (arg)
            {
                case "--": pastSeparator = true; continue;
                case "--json": json = true; continue;
                case "--yes" or "-y": yes = true; continue;
                case "--stream": stream = true; continue;
                case "--async": async = true; continue;
                case "--help" or "-h": help = true; continue;
                case "--version": version = true; continue;
            }
            if (arg.StartsWith("--timeout", StringComparison.Ordinal))
            {
                var value = arg.StartsWith("--timeout=", StringComparison.Ordinal)
                    ? arg["--timeout=".Length..]
                    : index + 1 < args.Length ? args[++index] : null;
                if (!int.TryParse(value, out var parsed) || parsed <= 0)
                    throw new ArgumentException("--timeout 需要正整数秒数。");
                timeoutSeconds = parsed;
                continue;
            }
            tokens.Add(arg);
        }

        return new CliInvocation(tokens, json, yes, stream, async, timeoutSeconds, help, version);
    }
}
