using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginLua;

/// <summary>
/// 脚本当前可用的宿主能力集合。
/// 加载阶段来自 <see cref="IPluginContext"/>，命令执行阶段来自 <see cref="PluginCommandContext"/>，
/// 两者成员一致，脚本侧看到的 API 完全相同。
/// </summary>
public sealed record LuaServices(
    IPluginLogger Logger,
    IPluginStorage Storage,
    IPluginConfiguration Configuration,
    IPluginAiClient AI);

/// <summary>
/// 混合插件里“底层用 DLL”的那一半。
///
/// 脚本只能按命令名调用 DLL 中实现了 <see cref="IPluginCommand"/> 的能力，
/// 拿不到反射、拿不到进程对象；DLL 与脚本运行在同一个隔离的插件进程内。
/// </summary>
public interface ILuaDllBridge : IDisposable
{
    /// <summary>是否已成功挂载程序集。</summary>
    bool IsAvailable { get; }

    /// <summary>挂载失败原因（无程序集时为 null）。</summary>
    string? LoadError { get; }

    /// <summary>程序集里可被脚本调用的命令名。</summary>
    IReadOnlyCollection<string> CommandNames { get; }

    /// <summary>加载并初始化程序集。</summary>
    Task<bool> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>按名字取命令实现。</summary>
    bool TryGetCommand(string name, out IPluginCommand command);
}

/// <summary>没有 DLL 的纯脚本插件使用。</summary>
public sealed class NullLuaDllBridge : ILuaDllBridge
{
    public static readonly NullLuaDllBridge Instance = new();

    public bool IsAvailable => false;

    public string? LoadError => null;

    public IReadOnlyCollection<string> CommandNames => Array.Empty<string>();

    public Task<bool> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public bool TryGetCommand(string name, out IPluginCommand command)
    {
        command = null!;
        return false;
    }

    public void Dispose()
    {
    }
}
