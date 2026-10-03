using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Interfaces;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// ViewModel for plugin permission request dialog
/// </summary>
public partial class PluginPermissionDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pluginName = string.Empty;

    [ObservableProperty]
    private string _pluginVersion = string.Empty;

    [ObservableProperty]
    private string _pluginAuthor = string.Empty;

    [ObservableProperty]
    private string _pluginDescription = string.Empty;

    [ObservableProperty]
    private string _permissionMode = "Sandbox";

    [ObservableProperty]
    private string _permissionModeIcon = "🔒";

    [ObservableProperty]
    private ObservableCollection<PermissionItemViewModel> _requestedPermissions = new();

    [ObservableProperty]
    private bool _isFullAccess;

    [ObservableProperty]
    private bool _showWarning;

    [ObservableProperty]
    private string _warningMessage = string.Empty;

    [ObservableProperty]
    private bool _isManagementMode;

    public PluginPermissionDialogResult Result { get; private set; } = PluginPermissionDialogResult.Deny;

    public PluginPermissionDialogViewModel()
    {
    }

    public void Initialize(string pluginName, string version, string author, string description,
        string permissionMode, IEnumerable<string> requestedPermissions)
    {
        PluginName = pluginName;
        PluginVersion = version;
        PluginAuthor = author;
        PluginDescription = description;
        PermissionMode = permissionMode;
        IsFullAccess = permissionMode == "FullAccess";
        PermissionModeIcon = IsFullAccess ? "🔓" : "🔒";

        RequestedPermissions.Clear();
        foreach (var permission in requestedPermissions)
        {
            RequestedPermissions.Add(new PermissionItemViewModel
            {
                Permission = permission,
                DisplayName = GetPermissionDisplayName(permission),
                Description = GetPermissionDescription(permission),
                Icon = GetPermissionIcon(permission),
                IsHighRisk = IsHighRiskPermission(permission)
            });
        }

        // Show warning for Full Access or high-risk permissions
        ShowWarning = IsFullAccess || RequestedPermissions.Any(p => p.IsHighRisk);
        if (IsFullAccess)
        {
            WarningMessage = "⚠️ This plugin requests Full Access mode, which grants unrestricted system access. Only allow plugins from trusted sources.";
        }
        else if (RequestedPermissions.Any(p => p.IsHighRisk))
        {
            WarningMessage = "⚠️ This plugin requests potentially dangerous permissions. Review carefully before allowing.";
        }
    }

    public void InitializeManagement(PluginItemViewModel plugin)
    {
        PluginName = plugin.Name;
        PluginVersion = plugin.Version;
        PluginAuthor = plugin.Author;
        PluginDescription = plugin.Description;
        PermissionMode = plugin.PermissionMode;
        IsFullAccess = plugin.PermissionMode == "FullAccess";
        PermissionModeIcon = IsFullAccess ? "🔓" : "🔒";
        IsManagementMode = true;
        RequestedPermissions.Clear();

        foreach (var permission in plugin.Permissions)
        {
            RequestedPermissions.Add(new PermissionItemViewModel
            {
                Permission = permission.Permission,
                DisplayName = GetPermissionDisplayName(permission.Permission),
                Description = GetPermissionDescription(permission.Permission),
                Icon = GetPermissionIcon(permission.Permission),
                IsHighRisk = IsHighRiskPermission(permission.Permission),
                IsGranted = permission.Granted,
                CanEdit = !IsFullAccess
            });
        }

        ShowWarning = IsFullAccess || RequestedPermissions.Any(p => p.IsHighRisk);
        WarningMessage = IsFullAccess
            ? "⚠️ Full Access 模式绕过权限列表检查，只有可信插件才应保持此模式。"
            : "修改权限后，插件下次访问资源时会按新的授权状态执行。";
    }

    [RelayCommand]
    private void AllowOnce(object? window)
    {
        Result = PluginPermissionDialogResult.AllowOnce;
        CloseWindow(window);
    }

    [RelayCommand]
    private void AllowAlways(object? window)
    {
        Result = PluginPermissionDialogResult.AllowAlways;
        CloseWindow(window);
    }

    [RelayCommand]
    private void Deny(object? window)
    {
        Result = PluginPermissionDialogResult.Deny;
        CloseWindow(window);
    }

    [RelayCommand]
    private void SavePermissions(object? window)
    {
        Result = PluginPermissionDialogResult.AllowAlways;
        CloseWindow(window);
    }

    [RelayCommand]
    private void CancelEdit(object? window)
    {
        Result = PluginPermissionDialogResult.Deny;
        CloseWindow(window);
    }

    private void CloseWindow(object? window)
    {
        if (window is Avalonia.Controls.Window w)
        {
            w.Close();
        }
    }

    private static string GetPermissionDisplayName(string permission)
    {
        return permission switch
        {
            "ReadModFiles" => "Read Mod Files",
            "WriteModFiles" => "Write Mod Files",
            "AccessNetwork" => "Network Access",
            "ExecuteProcesses" => "Execute External Programs",
            "ReadDatabase" => "Read Database",
            "WriteConfiguration" => "Write Configuration",
            "FileSystemAccess" => "File System Access",
            "RegistryAccess" => "Registry Access",
            "mods.read" => "读取模组文件",
            "mods.write" => "写入模组文件",
            "storage.read" => "读取插件存储",
            "storage.write" => "写入插件存储",
            "config.write" => "写入配置",
            "network" => "网络访问",
            _ => permission
        };
    }

    private static string GetPermissionDescription(string permission)
    {
        return permission switch
        {
            "ReadModFiles" => "Read mod metadata and file information",
            "WriteModFiles" => "Modify mod files and metadata",
            "AccessNetwork" => "Make network requests to external services",
            "ExecuteProcesses" => "Run external programs and scripts",
            "ReadDatabase" => "Query mod library and profile data",
            "WriteConfiguration" => "Modify application settings",
            "FileSystemAccess" => "Read and write files outside plugin directory",
            "RegistryAccess" => "Read and modify Windows registry",
            "mods.read" => "读取模组元数据和文件信息",
            "mods.write" => "修改模组文件和元数据",
            "storage.read" => "读取插件专用存储",
            "storage.write" => "写入插件专用存储",
            "config.write" => "修改插件配置",
            "network" => "向外部服务发起网络请求",
            _ => "Custom permission"
        };
    }

    private static string GetPermissionIcon(string permission)
    {
        return permission switch
        {
            "ReadModFiles" => "📖",
            "WriteModFiles" => "✏️",
            "AccessNetwork" => "🌐",
            "ExecuteProcesses" => "⚙️",
            "ReadDatabase" => "💾",
            "WriteConfiguration" => "🔧",
            "FileSystemAccess" => "📁",
            "RegistryAccess" => "🔑",
            "mods.read" => "📖",
            "mods.write" => "✏️",
            "storage.read" => "📁",
            "storage.write" => "📁",
            "config.write" => "🔧",
            "network" => "🌐",
            _ => "🔒"
        };
    }

    private static bool IsHighRiskPermission(string permission)
    {
        return permission switch
        {
            "ExecuteProcesses" => true,
            "WriteModFiles" => true,
            "FileSystemAccess" => true,
            "RegistryAccess" => true,
            "mods.write" => true,
            "storage.write" => true,
            _ => false
        };
    }
}

/// <summary>
/// Individual permission item for display
/// </summary>
public partial class PermissionItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _permission = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private bool _isHighRisk;

    [ObservableProperty]
    private bool _isGranted;

    [ObservableProperty]
    private bool _canEdit;
}

/// <summary>
/// Result of plugin permission dialog
/// </summary>
public enum PluginPermissionDialogResult
{
    Deny,
    AllowOnce,
    AllowAlways
}
