namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 插件的实现形态。两种形态跑在同一套进程隔离与权限模型下：
/// 都是独立进程、都只能通过 IPC 与主程序交互、都拿不到密钥。
///
/// 差异只在“谁来写业务逻辑”：
/// - <see cref="DotNet"/>：编译成 DLL，实现 IPlugin / IPluginCommand；
/// - <see cref="Lua"/>：写 Lua 脚本，由宿主内置的解释器执行，无需编译；
///   需要底层能力（文件格式解析、原生互操作、重计算）时可以再挂一个 DLL，
///   由脚本通过 <c>hd2.dll.call</c> 调用，即混合编写。
/// </summary>
public static class PluginKinds
{
    /// <summary>编译型插件，入口是程序集里的 <see cref="PluginManifest.EntryType"/>。</summary>
    public const string DotNet = "dotnet";

    /// <summary>脚本型插件，入口是 <see cref="PluginManifest.Script"/> 指向的 .lua 文件。</summary>
    public const string Lua = "lua";

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
            "dotnet" or "dll" or "assembly" or "csharp" or "cs" => DotNet,
            _ => DotNet
        };
    }

    /// <summary>是否为脚本型插件。</summary>
    public static bool IsLua(string? kind) => Normalize(kind) == Lua;

    /// <summary>人类可读的描述，用于日志与界面。</summary>
    public static string Describe(string? kind) => IsLua(kind) ? "Lua 脚本" : ".NET 程序集";
}
