using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Core.IPC;

/// <summary>
/// CLI 管道协议：帧常量、作用域派生与请求/响应帧类型。
/// AOT 薄客户端（HD2Mimnt.Cli）与托管服务端（HD2Mimnt.Agent / HD2Mimnt.UI）共用本文件，
/// 因此帧类型保持无依赖 POCO。帧 = 4 字节小端长度前缀 + UTF-8 JSON（与 PipeFrame 同风格）。
/// 流式响应在同一连接上先发 progress/prompt 帧、最后发 result 帧；非流式响应只有 result 一帧。
/// </summary>
public static class CliPipeProtocol
{
    /// <summary>管道帧中的单请求上限。</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>管道帧中的单响应上限（流式模式下作用于每个帧）。</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>响应帧类型标记：进度事件（仅流式请求）。</summary>
    public const string FrameProgress = "progress";

    /// <summary>响应帧类型标记：终态结果。旧式单帧响应没有 t 字段，也视为 result。</summary>
    public const string FrameResult = "result";

    /// <summary>响应帧类型标记：服务端向客户端发起的交互提问（压缩包密码、导入确认等）。</summary>
    public const string FramePrompt = "prompt";

    /// <summary>请求帧类型标记：控制命令（如 daemon shutdown），服务端处理后不进入命令分发。</summary>
    public const string FrameControl = "control";

    /// <summary>与 UI 的 BrowserProtocolHost.GetScope 相同的作用域：同一用户 + 同一数据目录。</summary>
    public static string GetScope()
    {
        var data = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        var directory = string.IsNullOrWhiteSpace(data)
            ? RuntimeDataPaths.GetDefaultApplicationDataPath()
            : Path.GetFullPath(data);
        return Environment.UserDomainName + "\\" + Environment.UserName + ":" + directory.TrimEnd('\\', '/').ToUpperInvariant();
    }

    private static string Hash(string scope)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));

    /// <summary>UI 进程监听的 CLI 管道名（UI 运行时是首选服务端：与图形界面同一份内存状态）。</summary>
    public static string GetUiPipeName() => "HD2Mimnt-Cli-" + Hash(GetScope());

    /// <summary>Agent 守护进程监听的管道名（UI 未运行时的常驻服务端）。</summary>
    public static string GetAgentPipeName() => GetUiPipeName() + "-agent";
}

/// <summary>CLI → 服务端的命令请求包（JSON 序列化后走管道）。</summary>
/// <param name="Stream">客户端声明支持流式响应（progress/prompt 帧）；缺省 false 时服务端只回终态一帧。</param>
/// <param name="Async">长任务（导入/部署等）提交到后台任务中心，立即返回任务 ID；缺省 false 同步等待完成。</param>
public sealed record CliPipePacket(IReadOnlyList<string> Tokens, bool Json, bool Yes, bool Stream = false, bool Async = false);

/// <summary>服务端 → CLI 的终态结果帧；与 CliCommandResponse 字段一致，独立定义避免管道两端耦合序列化行为。</summary>
public sealed record CliPipeResult(int ExitCode, string Message, object? Data, bool RequiresYes);

/// <summary>服务端 → CLI 的进度帧（仅流式请求；人类模式下打到 stderr，JSON 模式下与 result 一起构成 NDJSON 流）。</summary>
public sealed record CliPipeProgress(string Stage, double? Percent, string? Current, string Message)
{
    /// <summary>帧类型标记；管道帧按 t 字段分派，result 帧不带 t（缺省即 result）。</summary>
    public string T => CliPipeProtocol.FrameProgress;
}

/// <summary>服务端 → CLI 的交互提问帧；客户端以 <see cref="CliPipePromptResponse"/> 应答。</summary>
public sealed record CliPipePrompt(string Id, string Kind, object? Payload)
{
    public string T => CliPipeProtocol.FramePrompt;
}

/// <summary>CLI → 服务端的交互应答帧。</summary>
public sealed record CliPipePromptResponse(string Id, string? Value, bool Confirmed, bool Cancelled)
{
    public string T => "prompt-response";
}

/// <summary>CLI → 服务端的控制帧（如 shutdown），服务端处理后直接应答并退出，不进入命令分发。</summary>
public sealed record CliPipeControl(string Command)
{
    public string T => CliPipeProtocol.FrameControl;
}
