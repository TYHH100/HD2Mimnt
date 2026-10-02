using HD2Mimnt.PluginSDK;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginContext that provides all plugin services
/// </summary>
public class PluginContext : IPluginContext
{
    public IPluginLogger Logger { get; }
    public IPluginStorage Storage { get; }
    public IPluginModAccess ModAccess { get; }
    public IPluginConfiguration Configuration { get; }

    public PluginContext(
        IPluginLogger logger,
        IPluginStorage storage,
        IPluginModAccess modAccess,
        IPluginConfiguration configuration)
    {
        Logger = logger;
        Storage = storage;
        ModAccess = modAccess;
        Configuration = configuration;
    }
}
