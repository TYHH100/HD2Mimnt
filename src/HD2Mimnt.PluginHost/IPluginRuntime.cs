using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// 宿主进程内的插件运行时抽象。编译型（DLL）与脚本型（Lua）实现同一组语义，
/// 上层命令分发、日志、错误回执不需要关心插件是用什么写的。
/// </summary>
public interface IPluginRuntime : IDisposable
{
    /// <summary>插件清单（读取自 manifest.json）。</summary>
    PluginManifest Manifest { get; }

    /// <summary>插件贡献的命令，键为命令名（忽略大小写）。</summary>
    IReadOnlyDictionary<string, IPluginCommand> Commands { get; }

    /// <summary>加载期诊断信息（命令实例化失败、声明与实现不一致等）。</summary>
    IReadOnlyList<string> Diagnostics { get; }

    /// <summary>是否出现过无法中断的执行；为 true 时宿主应回收进程。</summary>
    bool HasRunawayExecution { get; }

    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
