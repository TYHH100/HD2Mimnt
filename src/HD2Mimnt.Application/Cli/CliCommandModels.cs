using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Application.Cli;

/// <summary>CLI 全局选项解析后的命令请求；Arguments 是命令名之后的全部 token。</summary>
public sealed record CliCommandRequest(string Command, IReadOnlyList<string> Arguments, bool Json, bool Yes);

/// <summary>命令执行结果。ExitCode：0 成功，1 业务失败，2 用法/确认问题。</summary>
public sealed class CliCommandResponse
{
    public int ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>--json 输出的结构化数据；人类可读文本由 <see cref="Message"/> 承担。</summary>
    public object? Data { get; init; }
    /// <summary>true 表示因缺少 --yes 确认而拒绝执行。</summary>
    public bool RequiresYes { get; init; }

    public static CliCommandResponse Ok(string message, object? data = null) => new() { ExitCode = 0, Message = message, Data = data };
    public static CliCommandResponse Fail(string message, object? data = null) => new() { ExitCode = 1, Message = message, Data = data };
    public static CliCommandResponse Usage(string message) => new() { ExitCode = 2, Message = message };
    public static CliCommandResponse ConfirmRequired(string usage) => new()
    {
        ExitCode = 2, RequiresYes = true,
        Message = "该操作有破坏性，需要显式确认：在命令后追加 --yes 重新执行。\n" + usage
    };
}

/// <summary>命令元数据；RequiresYes 的命令在 catalog 分发层统一拦截。</summary>
public sealed record CliCommandDescriptor(string Name, string Usage, string Summary, bool RequiresYes = false);

/// <summary>handler 执行上下文；服务由宿主提供（UI 管道宿主传入运行中应用的容器）。</summary>
public sealed class CliCommandContext
{
    public required CliCommandRequest Request { get; init; }
    public required IServiceProvider Services { get; init; }

    public T Get<T>() where T : notnull
        => Services.GetRequiredService<T>();

    public T? GetOrDefault<T>() where T : class
        => Services.GetService<T>();
}

public delegate Task<CliCommandResponse> CliCommandHandler(CliCommandContext context, CancellationToken cancellationToken);
