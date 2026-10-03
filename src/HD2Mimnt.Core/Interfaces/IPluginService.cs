namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Plugin management service interface
/// </summary>
public interface IPluginService
{
    /// <summary>
    /// Install a plugin from manifest path
    /// </summary>
    Task<PluginOperationResult> InstallPluginAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    Task<PluginOperationResult> UninstallPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable a plugin
    /// </summary>
    Task<PluginOperationResult> EnablePluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disable a plugin
    /// </summary>
    Task<PluginOperationResult> DisablePluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get plugin by ID
    /// </summary>
    Task<Entities.Plugin?> GetPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all installed plugins
    /// </summary>
    Task<List<Entities.Plugin>> GetAllPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get enabled plugins
    /// </summary>
    Task<List<Entities.Plugin>> GetEnabledPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate plugin manifest
    /// </summary>
    Task<PluginValidationResult> ValidateManifestAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the user-facing manifest metadata before installation.
    /// </summary>
    Task<PluginManifestPreview?> GetManifestPreviewAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if plugin has a specific permission
    /// </summary>
    Task<bool> CheckPermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grant a permission to a plugin
    /// </summary>
    Task<PluginOperationResult> GrantPermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke a permission from a plugin
    /// </summary>
    Task<PluginOperationResult> RevokePermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all permissions for a plugin
    /// </summary>
    Task<List<Entities.PluginPermission>> GetPluginPermissionsAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a plugin command
    /// </summary>
    Task<PluginOperationResult> ExecutePluginCommandAsync(string pluginId, string command, string? arguments = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get plugin execution history
    /// </summary>
    Task<List<Entities.PluginExecutionHistory>> GetExecutionHistoryAsync(string pluginId, int limit = 100, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of plugin manifest validation
/// </summary>
public class PluginValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public static PluginValidationResult Valid()
    {
        return new PluginValidationResult { IsValid = true };
    }

    public static PluginValidationResult Invalid(params string[] errors)
    {
        return new PluginValidationResult
        {
            IsValid = false,
            Errors = errors.ToList()
        };
    }
}

public sealed class PluginManifestPreview
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PermissionMode { get; init; } = "Sandbox";
    public IReadOnlyList<string> RequestedPermissions { get; init; } = Array.Empty<string>();
}
