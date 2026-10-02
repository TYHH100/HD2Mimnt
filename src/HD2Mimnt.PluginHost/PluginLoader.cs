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
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(manifestJson);
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

        return _pluginInstance;
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
        _loadContext.Unload();
    }
}
