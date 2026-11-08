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
    /// 按用户在权限对话框中的勾选安装插件。
    /// <paramref name="consent"/> 为 null 时沿用默认策略（沙箱模式自动授予非敏感权限）。
    /// </summary>
    Task<PluginOperationResult> InstallPluginAsync(
        string manifestPath,
        PluginInstallConsent? consent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动插件宿主进程（启动前校验程序集哈希与安装记录一致）
    /// </summary>
    Task<PluginOperationResult> StartPluginAsync(string pluginId, CancellationToken cancellationToken = default);

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
    /// Get commands declared by the plugin manifest. Available without starting the plugin process.
    /// </summary>
    Task<List<PluginCommandInfo>> GetPluginCommandsAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 汇总所有已启用插件在指定位置（如 <c>modContextMenu</c>）贡献的命令。
    /// 未授权的命令也会返回，由界面决定禁用还是隐藏。
    /// </summary>
    Task<List<PluginCommandContribution>> GetContributedCommandsAsync(
        string placement,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 汇总所有已启用插件的声明式界面贡献（页面 / 菜单 / 浮窗 / 主题），
    /// 并附带每个插件的权限授予状态，供界面判断入口是否可用。
    /// </summary>
    Task<List<PluginUiBundle>> GetUiContributionsAsync(CancellationToken cancellationToken = default);

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

/// <summary>
/// Command contributed by a plugin, as declared in its manifest.
/// </summary>
public sealed class PluginCommandInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? RequiredPermission { get; init; }

    /// <summary>命令出现的位置，取值见 PluginSDK 的 PluginCommandPlacements。</summary>
    public string Placement { get; init; } = "pluginPage";
}

/// <summary>
/// 跨插件汇总的一条命令贡献，附带来源插件与授权状态。
/// </summary>
public sealed class PluginCommandContribution
{
    public string PluginId { get; init; } = string.Empty;
    public string PluginName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? RequiredPermission { get; init; }

    /// <summary>所需权限是否已授予；未授权时界面应禁用或隐藏该入口。</summary>
    public bool IsPermissionGranted { get; init; } = true;
}

/// <summary>
/// 安装时用户对权限的选择。
/// </summary>
public sealed class PluginInstallConsent
{
    /// <summary>true = 始终允许（写入数据库）；false = 仅本次允许（仅本进程有效）。</summary>
    public bool PersistGrants { get; init; } = true;

    /// <summary>用户在对话框中勾选的权限。</summary>
    public IReadOnlyCollection<string> GrantedPermissions { get; init; } = Array.Empty<string>();
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

/// <summary>
/// 单个插件的界面贡献及其授权状态。
/// 界面只读这些声明，渲染完全由管理器负责。
/// </summary>
public sealed class PluginUiBundle
{
    public string PluginId { get; init; } = string.Empty;

    public string PluginName { get; init; } = string.Empty;

    public PluginSDK.PluginUiContributions Ui { get; init; } = new();

    /// <summary>当前已授予的权限集合（含本次会话授权）。</summary>
    public IReadOnlyCollection<string> GrantedPermissions { get; init; } = Array.Empty<string>();

    /// <summary>未声明权限视为无需授权；已授予则返回 true。</summary>
    public bool HasPermission(string? permission)
        => string.IsNullOrWhiteSpace(permission)
           || GrantedPermissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}
