using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core;

namespace HD2Mimnt.Application.Cli;

/// <summary>管道协议常量：CLI 与 UI 宿主共用。作用域与 BrowserProtocolHost 一致，但使用独立管道名。</summary>
public static class CliProtocol
{
    /// <summary>管道帧中的单请求上限。</summary>
    public const int MaxRequestBytes = 64 * 1024;
    /// <summary>管道帧中的单响应上限。</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>与 UI 的 BrowserProtocolHost.GetScope 相同的作用域：同一用户 + 同一数据目录。</summary>
    public static string GetScope()
    {
        var data = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        var directory = string.IsNullOrWhiteSpace(data)
            ? RuntimeDataPaths.GetDefaultApplicationDataPath()
            : Path.GetFullPath(data);
        return Environment.UserDomainName + "\\" + Environment.UserName + ":" + directory.TrimEnd('\\', '/').ToUpperInvariant();
    }

    public static string GetPipeName()
        => "HD2Mimnt-Cli-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GetScope())));
}

/// <summary>CLI → UI 的请求包（JSON 序列化后走管道）。</summary>
public sealed record CliPipePacket(IReadOnlyList<string> Tokens, bool Json, bool Yes);

/// <summary>UI → CLI 的响应包；与 CliCommandResponse 字段一致，独立定义避免管道两端耦合序列化行为。</summary>
public sealed record CliPipeResult(int ExitCode, string Message, object? Data, bool RequiresYes);
