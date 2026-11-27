namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 插件的实现形态。DotNet 与 Lua 跑在独立宿主进程中，只能通过 IPC 与主程序交互；
/// Web 插件只提供本地网页，不启动宿主进程。
///
/// 差异只在“谁来写业务逻辑”：
/// - <see cref="DotNet"/>：编译成 DLL，实现 IPlugin / IPluginCommand；
/// - <see cref="Lua"/>：写 Lua 脚本，由宿主内置的解释器执行，无需编译；
///   需要底层能力时可以再挂一个 DLL，由脚本通过 <c>hd2.dll.call</c> 调用；
/// - <see cref="Web"/>：只写 HTML/CSS/JavaScript 静态页面，不需要 DLL 或脚本。
/// </summary>
public static class PluginKinds
{
    /// <summary>编译型插件，入口是程序集里的 <see cref="PluginManifest.EntryType"/>。</summary>
    public const string DotNet = "dotnet";

    /// <summary>脚本型插件，入口是 <see cref="PluginManifest.Script"/> 指向的 .lua 文件。</summary>
    public const string Lua = "lua";

    /// <summary>纯网页插件，只提供 WebView2 页面，没有命令运行时。</summary>
    public const string Web = "web";

    /// <summary>
    /// 归一化插件类型。缺省、空值或无法识别的取值一律按编译型处理，
    /// 保证旧插件（清单里没有 kind 字段）行为不变。
    /// </summary>
    public static string Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return DotNet;
        }

        return kind.Trim().ToLowerInvariant() switch
        {
            "lua" or "script" or "lua-script" or "moonsharp" => Lua,
            "web" or "html" or "webui" or "web-ui" => Web,
            "dotnet" or "dll" or "assembly" or "csharp" or "cs" => DotNet,
            _ => DotNet
        };
    }

    /// <summary>是否为脚本型插件。</summary>
    public static bool IsLua(string? kind) => Normalize(kind) == Lua;

    /// <summary>是否为纯网页插件。</summary>
    public static bool IsWeb(string? kind) => Normalize(kind) == Web;

    /// <summary>人类可读的描述，用于日志与界面。</summary>
    public static string Describe(string? kind) => Normalize(kind) switch
    {
        Lua => "Lua 脚本",
        Web => "静态网页",
        _ => ".NET 程序集"
    };
}
