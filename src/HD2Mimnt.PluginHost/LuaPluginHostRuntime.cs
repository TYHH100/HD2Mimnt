using HD2Mimnt.PluginLua;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>脚本型（含混合）插件运行时，把 Lua 引擎适配成与 DLL 插件一致的语义。</summary>
internal sealed class LuaPluginHostRuntime : IPluginRuntime
{
    private readonly LuaPluginRuntime _runtime;
    private readonly string _scriptPath;
    private readonly string _manifestPath;

    public LuaPluginHostRuntime(LuaPluginRuntime runtime, string scriptPath, string manifestPath)
    {
        _runtime = runtime;
        _scriptPath = scriptPath;
        _manifestPath = manifestPath;
    }

    public PluginManifest Manifest => _runtime.Manifest;

    public IReadOnlyDictionary<string, IPluginCommand> Commands => _runtime.Commands;

    public IReadOnlyList<string> Diagnostics => _runtime.LoadErrors;

    public bool HasRunawayExecution => _runtime.HasRunawayExecution;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => _runtime.InitializeAsync(_scriptPath, _manifestPath, context, cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
        => _runtime.ShutdownAsync(cancellationToken);

    public void Dispose() => _runtime.Dispose();
}
