using HD2Mimnt.PluginLua;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// 混合插件里 DLL 那一半的桥接实现。
///
/// DLL 只被当作“命令库”加载：不实例化 IPlugin 入口、不注入任何额外能力。
/// 脚本通过 <c>hd2.dll.call</c> 按名字调用其中实现了 IPluginCommand 的类，
/// 参数与返回值仍走同一套 JSON/声明式界面模型。
/// </summary>
internal sealed class PluginAssemblyBridge : ILuaDllBridge
{
    private readonly string _pluginId;
    private readonly string _assemblyPath;
    private PluginLoader? _loader;

    public PluginAssemblyBridge(string pluginId, string assemblyPath)
    {
        _pluginId = pluginId;
        _assemblyPath = assemblyPath;
    }

    public bool IsAvailable { get; private set; }

    public string? LoadError { get; private set; }

    public IReadOnlyCollection<string> CommandNames
        => _loader?.Commands.Keys.ToArray() ?? Array.Empty<string>();

    public async Task<bool> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_assemblyPath) || !File.Exists(_assemblyPath))
        {
            LoadError = $"程序集不存在：{_assemblyPath}";
            return false;
        }

        try
        {
            _loader = new PluginLoader($"{_pluginId}.dll");
            await _loader.LoadCommandLibraryAsync(_assemblyPath, cancellationToken).ConfigureAwait(false);

            if (_loader.CommandLoadErrors.Count > 0)
            {
                LoadError = string.Join("；", _loader.CommandLoadErrors);
                context.Logger.Warning($"[Lua] 程序集部分命令加载失败：{LoadError}");
            }

            IsAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            return false;
        }
    }

    public bool TryGetCommand(string name, out IPluginCommand command)
    {
        if (_loader is not null && _loader.TryGetCommand(name, out command))
        {
            return true;
        }

        command = null!;
        return false;
    }

    public void Dispose()
    {
        _loader?.Dispose();
        _loader = null;
        IsAvailable = false;
    }
}
