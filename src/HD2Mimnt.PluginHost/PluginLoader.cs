using System.Reflection;
using System.Runtime.Loader;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// Loads plugin assemblies in isolated AssemblyLoadContext
/// </summary>
public class PluginLoader : IDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly Dictionary<string, IPluginCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private Assembly? _pluginAssembly;
    private IPlugin? _pluginInstance;

    public PluginLoader(string pluginId)
    {
        _loadContext = new AssemblyLoadContext($"Plugin_{pluginId}", isCollectible: true);
    }

    /// <summary>
    /// Load plugin assembly and create instance
    /// </summary>
    public async Task<IPlugin> LoadPluginAsync(string assemblyPath, string manifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Plugin manifest not found: {manifestPath}");
        }

        // Load manifest to get entry type
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = PluginManifestJson.Deserialize(manifestJson);
        if (manifest == null)
        {
            throw new InvalidOperationException("Failed to deserialize plugin manifest");
        }

        // Load assembly into isolated context
        _pluginAssembly = _loadContext.LoadFromAssemblyPath(assemblyPath);

        // Find entry type
        var entryType = _pluginAssembly.GetType(manifest.EntryType);
        if (entryType == null)
        {
            throw new TypeLoadException($"Entry type not found: {manifest.EntryType}");
        }

        // Verify it implements IPlugin
        if (!typeof(IPlugin).IsAssignableFrom(entryType))
        {
            throw new InvalidOperationException($"Entry type does not implement IPlugin: {manifest.EntryType}");
        }

        // Create instance
        _pluginInstance = Activator.CreateInstance(entryType) as IPlugin;
        if (_pluginInstance == null)
        {
            throw new InvalidOperationException($"Failed to create plugin instance: {manifest.EntryType}");
        }

        DiscoverCommands();

        return _pluginInstance;
    }

    /// <summary>
    /// 只加载命令库：不实例化 IPlugin 入口类型，仅扫描并注册 IPluginCommand。
    /// 混合插件里的 DLL 走的正是这条路——它提供底层能力，由脚本驱动。
    /// </summary>
    public Task LoadCommandLibraryAsync(string assemblyPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");
        }

        _pluginAssembly = _loadContext.LoadFromAssemblyPath(assemblyPath);
        DiscoverCommands();
        return Task.CompletedTask;
    }

    /// <summary>已发现的命令实现，键为 Descriptor.Name（忽略大小写）。</summary>
    public IReadOnlyDictionary<string, IPluginCommand> Commands => _commands;

    /// <summary>命令实例化失败的原因，供宿主记录日志；不阻断插件加载。</summary>
    public List<string> CommandLoadErrors { get; } = new();

    public bool TryGetCommand(string name, out IPluginCommand command)
        => _commands.TryGetValue(name, out command!);

    /// <summary>
    /// 扫描插件程序集中所有 IPluginCommand 实现并按 Descriptor.Name 注册。
    /// 单个命令构造失败只记录原因，不影响插件其余能力。
    /// </summary>
    private void DiscoverCommands()
    {
        _commands.Clear();
        CommandLoadErrors.Clear();

        if (_pluginAssembly == null)
        {
            return;
        }

        Type[] types;
        try
        {
            types = _pluginAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (!typeof(IPluginCommand).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is not IPluginCommand command)
                {
                    CommandLoadErrors.Add($"{type.FullName}: 无法创建命令实例");
                    continue;
                }

                var name = command.Descriptor?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    CommandLoadErrors.Add($"{type.FullName}: Descriptor.Name 为空");
                    continue;
                }

                if (!_commands.TryAdd(name, command))
                {
                    CommandLoadErrors.Add($"{type.FullName}: 命令名 '{name}' 重复");
                }
            }
            catch (Exception ex)
            {
                CommandLoadErrors.Add($"{type.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get loaded plugin instance
    /// </summary>
    public IPlugin? GetPlugin() => _pluginInstance;

    /// <summary>
    /// Unload plugin and cleanup
    /// </summary>
    public void Dispose()
    {
        _pluginInstance = null;
        _pluginAssembly = null;
        _commands.Clear();
        _loadContext.Unload();
    }
}
