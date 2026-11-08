namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Plugin manifest containing metadata and permissions
/// </summary>
public class PluginManifest
{
    /// <summary>
    /// Unique plugin identifier (e.g., "com.example.analysis")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the plugin
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Plugin version (SemVer format)
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Plugin author name or organization
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Plugin description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Required Plugin SDK API version
    /// </summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    /// Target .NET runtime (e.g., "net10.0")
    /// </summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>
    /// Permission mode: Sandbox or FullAccess
    /// </summary>
    public PluginPermissionMode PermissionMode { get; set; } = PluginPermissionMode.Sandbox;

    /// <summary>
    /// List of requested permissions
    /// </summary>
    public List<string> RequestedPermissions { get; set; } = new();

    /// <summary>
    /// Commands the plugin contributes to the host application.
    /// Declared here so the UI can list them before the plugin process starts;
    /// each entry must have a matching IPluginCommand implementation in the assembly.
    /// </summary>
    public List<PluginCommandDescriptor> Commands { get; set; } = new();

    /// <summary>
    /// 声明式界面贡献：页面、功能菜单、浮窗与主题包。
    /// 管理器读取声明并用自己的控件渲染，插件程序集不进入 UI 进程。
    /// </summary>
    public PluginUiContributions Ui { get; set; } = new();

    /// <summary>
    /// Assembly file name (e.g., "MyPlugin.dll")
    /// </summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified entry type name (e.g., "MyPlugin.MyPlugin")
    /// </summary>
    public string EntryType { get; set; } = string.Empty;
}

/// <summary>
/// Plugin permission mode
/// </summary>
public enum PluginPermissionMode
{
    /// <summary>
    /// Sandbox mode - restricted permissions (default)
    /// </summary>
    Sandbox,

    /// <summary>
    /// Full access mode - requires explicit user approval
    /// </summary>
    FullAccess
}
