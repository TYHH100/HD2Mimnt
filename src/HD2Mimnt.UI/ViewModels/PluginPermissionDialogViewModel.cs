using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
