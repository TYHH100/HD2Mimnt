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
    private string _permissionModeIcon = "沙";

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
        PermissionModeIcon = IsFullAccess ? "全" : "沙";

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
            WarningMessage = "此插件请求完全访问模式，可不受限制地访问系统资源。仅允许可信来源的插件。";
        }
        else if (RequestedPermissions.Any(p => p.IsHighRisk))
        {
            WarningMessage = "此插件请求潜在危险权限，请在允许前仔细检查。";
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
        PermissionModeIcon = IsFullAccess ? "全" : "沙";
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
            ? "完全访问模式绕过权限列表检查，只有可信插件才应保持此模式。"
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
            "ReadModFiles" => "读取模组文件",
            "WriteModFiles" => "写入模组文件",
            "AccessNetwork" => "网络访问",
            "ExecuteProcesses" => "执行外部程序",
            "ReadDatabase" => "读取数据库",
            "WriteConfiguration" => "写入配置",
            "FileSystemAccess" => "文件系统访问",
            "RegistryAccess" => "注册表访问",
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
            "ReadModFiles" => "读取模组元数据和文件信息",
            "WriteModFiles" => "修改模组文件和元数据",
            "AccessNetwork" => "向外部服务发起网络请求",
            "ExecuteProcesses" => "运行外部程序和脚本",
            "ReadDatabase" => "查询模组库和配置文件数据",
            "WriteConfiguration" => "修改应用设置",
            "FileSystemAccess" => "读写插件目录之外的文件",
            "RegistryAccess" => "读取和修改 Windows 注册表",
            "mods.read" => "读取模组元数据和文件信息",
            "mods.write" => "修改模组文件和元数据",
            "storage.read" => "读取插件专用存储",
            "storage.write" => "写入插件专用存储",
            "config.write" => "修改插件配置",
            "network" => "向外部服务发起网络请求",
            _ => "自定义权限"
        };
    }

    private static string GetPermissionIcon(string permission)
    {
        return permission switch
        {
            "ReadModFiles" or "mods.read" => "读",
            "WriteModFiles" or "mods.write" => "写",
            "AccessNetwork" or "network" => "网",
            "ExecuteProcesses" => "程",
            "ReadDatabase" => "库",
            "WriteConfiguration" or "config.write" => "配",
            "FileSystemAccess" or "storage.read" or "storage.write" => "文",
            "RegistryAccess" => "表",
            _ => "权"
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
