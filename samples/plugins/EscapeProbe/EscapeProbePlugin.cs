using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.EscapeProbe;

/// <summary>
/// 沙箱逃逸探针（编译型插件）。
///
/// 与 SandboxProbe（Lua）的分工：
/// - Lua 探针只能验证“脚本层暴露了哪些 API”，属于结构性检查；
/// - 本探针直接调用 .NET，验证“进程隔离到底有多强”，属于实证检查。
///
/// 本插件刻意保持 **0 权限 + Sandbox 模式**，却只调用原生 .NET 而完全不经过 SDK：
/// 如果这些调用能成功，说明权限清单与 Sandbox 模式约束的是 SDK 调用，而不是进程能力。
///
/// 所有探测都是只读或可回收的：不删文件、不写系统目录、不外传任何数据（联网只做 DNS 与 TCP 握手）。
/// </summary>
public sealed class EscapeProbePlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "com.hd2mimnt.samples.escape-probe",
        Name = "沙箱逃逸探针",
        Version = "1.0.0",
        Author = "HD2Mimnt Samples",
        Description = "以 0 权限的 Sandbox 插件身份直接调用 .NET，尝试越权读写文件、启动进程、枚举环境变量与外联，用于实证进程隔离强度。",
        ApiVersion = PluginSdkInfo.Version,
        Runtime = "net10.0",
        PermissionMode = PluginPermissionMode.Sandbox,
        Assembly = "EscapeProbe.dll",
        EntryType = typeof(EscapeProbePlugin).FullName!,
        Commands =
        {
            new PluginCommandDescriptor
            {
                Name = "escape-report",
                DisplayName = "跑全部逃逸项",
                Description = "依次尝试五类越界行为，汇总成一张判定表。"
            },
            new PluginCommandDescriptor
            {
                Name = "escape-file-read",
                DisplayName = "任意文件读取",
                Description = "绕过 IPluginStorage 直接读插件目录外的文件（只读长度，不输出内容）。"
            },
            new PluginCommandDescriptor
            {
                Name = "escape-file-write",
                DisplayName = "任意文件写入",
                Description = "绕过 IPluginStorage 直接往系统临时目录写文件，写完立即删除。"
            },
            new PluginCommandDescriptor
            {
                Name = "escape-process",
                DisplayName = "启动子进程",
                Description = "用 Process.Start 执行 whoami 与 hostname。"
            },
            new PluginCommandDescriptor
            {
                Name = "escape-env",
                DisplayName = "枚举环境变量",
                Description = "枚举进程环境变量名，统计疑似凭据的变量（只输出变量名，不输出值）。"
            },
            new PluginCommandDescriptor
            {
                Name = "escape-network",
                DisplayName = "外联测试",
                Description = "尝试 DNS 解析与 TCP 直连，验证进程是否具备出网能力。"
            }
        }
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Warning(
            "沙箱逃逸探针已初始化：本插件声明 Sandbox 模式且未申请任何权限，接下来它会尝试直接调用 .NET");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>单项探测的结果。</summary>
internal sealed record ProbeOutcome(string Command, string Vector, bool Escaped, string Detail);

/// <summary>
/// 把探测结果渲染成声明式界面。
/// 判定口径统一：成功越过 SDK 边界 = escaped。
/// </summary>
internal static class ProbeReport
{
    public static PluginResult Single(ProbeOutcome outcome)
    {
        var token = $"{outcome.Command}:{(outcome.Escaped ? "escaped" : "contained")}";
        return PluginResult.Ok(token, PluginUiUpdate.Replace(BuildBlocks(new[] { outcome }).ToArray()));
    }

    public static PluginResult Summary(IReadOnlyList<ProbeOutcome> outcomes)
    {
        var escaped = outcomes.Count(o => o.Escaped);
        var token = $"escape-report:{escaped}/{outcomes.Count}";
        return PluginResult.Ok(token, PluginUiUpdate.Replace(BuildBlocks(outcomes).ToArray()));
    }

    private static List<PluginUiBlock> BuildBlocks(IReadOnlyList<ProbeOutcome> outcomes)
    {
        var lines = new List<string>
        {
            "## 进程隔离实测",
            "",
            "| 探测项 | 判定 | 细节 |",
            "| --- | --- | --- |"
        };

        foreach (var outcome in outcomes)
        {
            var verdict = outcome.Escaped ? "**越界成功**" : "被拦住";
            lines.Add($"| {outcome.Vector} | {verdict} | {outcome.Detail} |");
        }

        var blocks = new List<PluginUiBlock>
        {
            new() { Type = PluginUiBlockTypes.Markdown, Text = string.Join("\n", lines) }
        };

        var escapedCount = outcomes.Count(o => o.Escaped);
        blocks.Add(escapedCount > 0
            ? new PluginUiBlock
            {
                Type = PluginUiBlockTypes.Notice,
                Tone = PluginUiTones.Danger,
                Title = "进程能力未被隔离",
                Text = $"{escapedCount} / {outcomes.Count} 项越界成功。说明当前的 Sandbox 模式与权限清单约束的是 SDK 调用，"
                       + "而不是进程能力——插件与主程序同用户同权限运行。"
            }
            : new PluginUiBlock
            {
                Type = PluginUiBlockTypes.Notice,
                Tone = PluginUiTones.Success,
                Title = "未观察到越界",
                Text = "所有探测项都被拒绝。若与预期不符，先确认宿主是否真的以受限身份启动插件进程。"
            });

        blocks.Add(new PluginUiBlock
        {
            Type = PluginUiBlockTypes.Markdown,
            Text = "> 口径说明：`越界成功` 指绕过 SDK 直接触达文件系统 / 进程 / 网络。\n"
                   + "> 本探针不输出任何文件内容与凭据值，联网只做 DNS 解析与 TCP 握手，不发送应用数据。"
        });

        return blocks;
    }
}

/// <summary>五类逃逸尝试的具体实现。所有动作都在 try/catch 内，单项失败不影响其他项。</summary>
internal static class EscapeVectors
{
    /// <summary>任意文件读取：绕过 IPluginStorage 直接开文件，只回读长度。</summary>
    public static ProbeOutcome FileReadVector()
    {
        var candidates = new[]
        {
            @"C:\Windows\win.ini",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HD2Mimnt",
                "storage-locations.json")
        };

        var readable = new List<string>();
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var length = new FileInfo(path).Length;
                using var stream = File.OpenRead(path);
                var buffer = new byte[Math.Min(64, (int)Math.Min(length, 64))];
                if (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                    readable.Add($"{Path.GetFileName(path)}（{length} 字节）");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // 读不到就是被拦住
            }
        }

        return readable.Count > 0
            ? new ProbeOutcome("escape-file-read", "任意文件读取", true,
                "已直接读到：" + string.Join("、", readable) + "（只取长度，未输出内容）")
            : new ProbeOutcome("escape-file-read", "任意文件读取", false, "候选文件均不可读");
    }

    /// <summary>任意文件写入：写到系统临时目录，写完立刻删除。</summary>
    public static ProbeOutcome FileWriteVector()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2-escape-probe-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "sandbox escape probe");
            var length = new FileInfo(path).Length;
            File.Delete(path);
            return new ProbeOutcome("escape-file-write", "任意文件写入", true,
                $"已在临时目录写出 {length} 字节并回收：{Path.GetDirectoryName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new ProbeOutcome("escape-file-write", "任意文件写入", false, ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>启动子进程：执行 whoami 与 hostname。</summary>
    public static ProbeOutcome ProcessVector()
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c whoami & hostname",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProbeOutcome("escape-process", "启动子进程", false, "Process.Start 返回 null");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);

            var oneLine = output.Replace("\r\n", " / ").Replace("\n", " / ");
            var escaped = oneLine.Length > 0 && process.HasExited && process.ExitCode == 0;

            return new ProbeOutcome("escape-process", "启动子进程", escaped,
                escaped ? $"子进程输出：{oneLine}" : $"子进程未正常退出（exit={process.ExitCode}）");
        }
        catch (Exception ex)
        {
            return new ProbeOutcome("escape-process", "启动子进程", false, ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>枚举环境变量：只看变量名，绝不读值。</summary>
    public static ProbeOutcome EnvironmentVector()
    {
        try
        {
            var keys = System.Environment.GetEnvironmentVariables().Keys
                .Cast<string>()
                .ToArray();

            string[] markers = { "KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "CONNECTIONSTRING" };
            var suspicious = keys
                .Where(key => markers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var detail = suspicious.Length > 0
                ? $"{keys.Length} 个变量，其中疑似凭据变量 {suspicious.Length} 个：{string.Join("、", suspicious.Take(6))}"
                : $"{keys.Length} 个变量，未发现疑似凭据变量";

            return new ProbeOutcome("escape-env", "枚举环境变量", true, detail + "（只统计变量名，未读取任何值）");
        }
        catch (Exception ex)
        {
            return new ProbeOutcome("escape-env", "枚举环境变量", false, ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>外联：DNS 解析 + TCP 握手，不发送任何应用层数据。</summary>
    public static async Task<ProbeOutcome> NetworkVectorAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        var details = new List<string>();
        var escaped = false;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync("example.com", timeout.Token).ConfigureAwait(false);
            if (addresses.Length > 0)
            {
                escaped = true;
                details.Add($"DNS 解析 example.com 成功（{addresses.Length} 个地址）");
            }
        }
        catch (Exception ex)
        {
            details.Add("DNS 解析失败：" + ex.GetType().Name);
        }

        try
        {
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync("1.1.1.1", 443, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (client.Connected)
            {
                escaped = true;
                details.Add($"TCP 1.1.1.1:443 连接成功（{stopwatch.ElapsedMilliseconds} ms，未发送应用数据）");
            }
        }
        catch (OperationCanceledException)
        {
            details.Add("TCP 连接 3 秒内未完成（超时）——可能是网络策略阻断，不代表沙箱拦截");
        }
        catch (Exception ex)
        {
            details.Add("TCP 连接失败：" + ex.GetType().Name);
        }

        return new ProbeOutcome("escape-network", "外联（DNS + TCP）", escaped, string.Join("；", details));
    }
}

/// <summary>单个命令的薄封装：统一异常兜底，单项失败也返回可读结果而不是让宿主崩掉。</summary>
internal static class ProbeRunner
{
    public static async Task<PluginResult> RunAsync(
        string command,
        Func<CancellationToken, Task<ProbeOutcome>> probe,
        CancellationToken cancellationToken)
    {
        try
        {
            return ProbeReport.Single(await probe(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ProbeReport.Single(new ProbeOutcome(command, command, false, $"{ex.GetType().Name}：{ex.Message}"));
        }
    }
}

public sealed class EscapeFileReadCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-file-read",
        DisplayName = "任意文件读取"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => ProbeRunner.RunAsync(Descriptor.Name, _ => Task.FromResult(EscapeVectors.FileReadVector()), cancellationToken);
}

public sealed class EscapeFileWriteCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-file-write",
        DisplayName = "任意文件写入"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => ProbeRunner.RunAsync(Descriptor.Name, _ => Task.FromResult(EscapeVectors.FileWriteVector()), cancellationToken);
}

public sealed class EscapeProcessCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-process",
        DisplayName = "启动子进程"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => ProbeRunner.RunAsync(Descriptor.Name, _ => Task.FromResult(EscapeVectors.ProcessVector()), cancellationToken);
}

public sealed class EscapeEnvironmentCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-env",
        DisplayName = "枚举环境变量"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => ProbeRunner.RunAsync(Descriptor.Name, _ => Task.FromResult(EscapeVectors.EnvironmentVector()), cancellationToken);
}

public sealed class EscapeNetworkCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-network",
        DisplayName = "外联测试"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => ProbeRunner.RunAsync(Descriptor.Name, ct => EscapeVectors.NetworkVectorAsync(ct), cancellationToken);
}

/// <summary>汇总命令：把五类尝试一次性跑完。</summary>
public sealed class EscapeReportCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "escape-report",
        DisplayName = "跑全部逃逸项"
    };

    public async Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ProbeOutcome>
        {
            Safe("escape-file-read", () => EscapeVectors.FileReadVector()),
            Safe("escape-file-write", () => EscapeVectors.FileWriteVector()),
            Safe("escape-process", () => EscapeVectors.ProcessVector()),
            Safe("escape-env", () => EscapeVectors.EnvironmentVector())
        };

        try
        {
            outcomes.Add(await EscapeVectors.NetworkVectorAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            outcomes.Add(new ProbeOutcome("escape-network", "外联（DNS + TCP）", false, ex.GetType().Name + "：" + ex.Message));
        }

        context.Logger.Warning($"逃生探针完成：{outcomes.Count(o => o.Escaped)} / {outcomes.Count} 项越界成功");
        return ProbeReport.Summary(outcomes);
    }

    private static ProbeOutcome Safe(string command, Func<ProbeOutcome> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            return new ProbeOutcome(command, command, false, ex.GetType().Name + "：" + ex.Message);
        }
    }
}
