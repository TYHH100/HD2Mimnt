namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Base interface that all plugins must implement
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Plugin manifest containing metadata and permissions
    /// </summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// Initialize the plugin with the provided context
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shutdown the plugin and release resources
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Plugin that handles mod lifecycle events
/// </summary>
public interface IModPlugin : IPlugin
{
    /// <summary>
    /// Called when a mod is imported into the library
    /// </summary>
    Task<PluginResult> OnModImportedAsync(PluginModInfo mod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when a mod is deleted from the library
    /// </summary>
    Task<PluginResult> OnModDeletedAsync(PluginModInfo mod, CancellationToken cancellationToken = default);
}

/// <summary>
/// Plugin that handles deployment events
/// </summary>
public interface IDeploymentPlugin : IPlugin
{
    /// <summary>
    /// Called before deployment starts
    /// </summary>
    Task<PluginResult> OnPreDeployAsync(PluginDeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after deployment completes
    /// </summary>
    Task<PluginResult> OnPostDeployAsync(PluginDeploymentContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Plugin that provides mod analysis capabilities
/// </summary>
public interface IAnalysisPlugin : IPlugin
{
    /// <summary>
    /// Analyze a mod and return analysis results
    /// </summary>
    Task<PluginAnalysisResult> AnalyzeModAsync(PluginModInfo mod, CancellationToken cancellationToken = default);
}
