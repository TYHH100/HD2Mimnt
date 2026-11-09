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
    /// Assembly file name (e.g., "MyPlugin.dll")。
    /// 脚本型插件可以留空；填写时为“混合插件”，脚本通过 hd2.dll.call 调用该程序集里的命令。
    /// </summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified entry type name (e.g., "MyPlugin.MyPlugin")。
    /// 仅编译型插件必需；脚本型与混合型留空。
    /// </summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>
    /// 实现形态，取值见 <see cref="PluginKinds"/>：dotnet（默认）或 lua。
    /// 缺省按 dotnet 处理，旧清单无需改动。
    /// </summary>
    public string Kind { get; set; } = PluginKinds.DotNet;

    /// <summary>
    /// 脚本型插件的入口文件（相对清单目录，如 "main.lua"）。
    /// 其它 .lua 文件可用 hd2.require("模块名") 按需加载。
    /// </summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>是否为脚本型插件。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsScriptPlugin => PluginKinds.IsLua(Kind);

    /// <summary>是否为“脚本主控 + DLL 提供底层能力”的混合插件。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHybrid => IsScriptPlugin && !string.IsNullOrWhiteSpace(Assembly);

    /// <summary>
    /// 插件入口文件（相对清单目录）。脚本型返回 <see cref="Script"/>，
    /// 编译型返回 <see cref="Assembly"/>。安装与完整性校验都以它为准。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EntryFile => IsScriptPlugin ? Script : Assembly;
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
