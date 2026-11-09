using HD2Mimnt.PluginHost.IPC;
using HD2Mimnt.PluginLua;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// 按 manifest 的 kind 选择运行时：
/// 缺省或 dotnet → 加载程序集；lua → 启动脚本解释器（可再挂一个命令库 DLL）。
/// </summary>
internal static class PluginRuntimeFactory
{
    public static async Task<IPluginRuntime> CreateAsync(
        string pluginId,
        LoadPluginCommand command,
        PluginHostLogger logger,
        CancellationToken cancellationToken = default)
    {
        var manifestJson = await File.ReadAllTextAsync(command.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = PluginManifestJson.Deserialize(manifestJson)
            ?? throw new InvalidOperationException("插件清单解析失败");

        if (!PluginKinds.IsLua(manifest.Kind))
        {
            var loader = new PluginLoader(pluginId);
            var plugin = await loader.LoadPluginAsync(command.PluginPath, command.ManifestPath, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetPluginRuntime(loader, plugin);
        }

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(command.ManifestPath)) ?? ".";
        var scriptPath = string.IsNullOrWhiteSpace(manifest.Script)
            ? command.PluginPath
            : Path.GetFullPath(Path.Combine(manifestDirectory, manifest.Script));

        ILuaDllBridge dll = NullLuaDllBridge.Instance;
        if (manifest.IsHybrid)
        {
            var assemblyPath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.Assembly));
            dll = new PluginAssemblyBridge(pluginId, assemblyPath);
        }

        var runtime = new LuaPluginRuntime(pluginId, dll, message => logger.Info($"[Lua] {message}"));
        logger.Info($"脚本插件已装载：{Path.GetFileName(scriptPath)}（{PluginKinds.Describe(manifest.Kind)}）");

        return new LuaPluginHostRuntime(runtime, scriptPath, command.ManifestPath);
    }
}
