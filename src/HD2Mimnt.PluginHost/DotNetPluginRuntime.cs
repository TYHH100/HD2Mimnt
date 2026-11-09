using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>编译型插件：加载程序集、实例化入口类型并扫描命令。</summary>
internal sealed class DotNetPluginRuntime : IPluginRuntime
{
    private readonly PluginLoader _loader;
    private IPlugin? _plugin;

    public DotNetPluginRuntime(PluginLoader loader, IPlugin plugin)
    {
        _loader = loader;
        _plugin = plugin;
    }

    public PluginManifest Manifest => _plugin?.Manifest ?? new PluginManifest();

    public IReadOnlyDictionary<string, IPluginCommand> Commands => _loader.Commands;

    /// <summary>命令实例化失败 + 声明了但没有实现，两类问题都算加载诊断。</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            var diagnostics = new List<string>(_loader.CommandLoadErrors);
            foreach (var declared in Manifest.Commands)
            {
                if (!Commands.ContainsKey(declared.Name))
                {
                    diagnostics.Add($"命令 '{declared.Name}' 已在 manifest 声明，但程序集中没有对应实现");
                }
            }
            return diagnostics;
        }
    }

    public bool HasRunawayExecution => false;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        if (_plugin is null)
        {
            throw new InvalidOperationException("插件实例未创建");
        }

        await _plugin.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_plugin is { } plugin)
        {
            await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _plugin = null;
        _loader.Dispose();
    }
}
