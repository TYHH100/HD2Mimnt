using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// ViewModel for Plugin Management page in Settings
/// </summary>
public partial class PluginManagementViewModel : ObservableObject
{
    private readonly IPluginService _pluginService;

    [ObservableProperty]
    private ObservableCollection<PluginItemViewModel> _installedPlugins = new();

    [ObservableProperty]
    private PluginItemViewModel? _selectedPlugin;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _totalPlugins;

    [ObservableProperty]
    private int _enabledPlugins;

    [ObservableProperty]
    private int _disabledPlugins;

    public PluginManagementViewModel(IPluginService pluginService)
    {
        _pluginService = pluginService;
    }

    public async Task InitializeAsync()
    {
        await LoadPluginsAsync();
    }

    [RelayCommand]
    private async Task LoadPluginsAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading plugins...";

        try
        {
            var plugins = await _pluginService.GetAllPluginsAsync();

            InstalledPlugins.Clear();
            foreach (var plugin in plugins)
            {
                var permissions = await _pluginService.GetPluginPermissionsAsync(plugin.Id);
                InstalledPlugins.Add(new PluginItemViewModel(plugin, permissions.ToList()));
            }

            UpdateStatistics();
            StatusMessage = $"Loaded {InstalledPlugins.Count} plugins";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load plugins: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallPluginAsync()
    {
        // TODO: Show file picker dialog for plugin manifest
        StatusMessage = "Plugin installation coming soon...";
    }

    [RelayCommand]
    private async Task EnablePluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        IsLoading = true;
        StatusMessage = $"Enabling {plugin.Name}...";

        try
        {
            var result = await _pluginService.EnablePluginAsync(plugin.PluginId);
            if (result.Success)
            {
                plugin.IsEnabled = true;
                UpdateStatistics();
                StatusMessage = $"Enabled {plugin.Name}";
            }
            else
            {
                StatusMessage = $"Failed to enable {plugin.Name}: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DisablePluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        IsLoading = true;
        StatusMessage = $"Disabling {plugin.Name}...";

        try
        {
            var result = await _pluginService.DisablePluginAsync(plugin.PluginId);
            if (result.Success)
            {
                plugin.IsEnabled = false;
                UpdateStatistics();
                StatusMessage = $"Disabled {plugin.Name}";
            }
            else
            {
                StatusMessage = $"Failed to disable {plugin.Name}: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UninstallPluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        // TODO: Show confirmation dialog

        IsLoading = true;
        StatusMessage = $"Uninstalling {plugin.Name}...";

        try
        {
            var result = await _pluginService.UninstallPluginAsync(plugin.PluginId);
            if (result.Success)
            {
                InstalledPlugins.Remove(plugin);
                UpdateStatistics();
                StatusMessage = $"Uninstalled {plugin.Name}";
            }
            else
            {
                StatusMessage = $"Failed to uninstall {plugin.Name}: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ViewExecutionHistoryAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        try
        {
            var history = await _pluginService.GetExecutionHistoryAsync(plugin.PluginId, 50);
            // TODO: Show execution history dialog
            StatusMessage = $"Found {history.Count} execution records for {plugin.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadPluginsAsync();
    }

    private void UpdateStatistics()
    {
        TotalPlugins = InstalledPlugins.Count;
        EnabledPlugins = InstalledPlugins.Count(p => p.IsEnabled);
        DisabledPlugins = InstalledPlugins.Count(p => !p.IsEnabled);
    }
}

/// <summary>
/// ViewModel for individual plugin item in the list
/// </summary>
public partial class PluginItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pluginId;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _version;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _permissionMode;

    [ObservableProperty]
    private DateTime _installedAt;

    [ObservableProperty]
    private string _assemblyPath;

    [ObservableProperty]
    private ObservableCollection<PluginPermission> _permissions = new();

    public string InstalledAtFormatted => InstalledAt.ToString("yyyy-MM-dd HH:mm");

    public string StatusText => IsEnabled ? "Enabled" : "Disabled";

    public string StatusIcon => IsEnabled ? "✅" : "⏸️";

    public string PermissionModeIcon => PermissionMode == "FullAccess" ? "🔓" : "🔒";

    public string PermissionModeDisplay => PermissionMode == "FullAccess" ? "Full Access" : "Sandbox";

    public int GrantedPermissionsCount => Permissions.Count(p => p.Granted);

    public PluginItemViewModel(Plugin plugin, List<PluginPermission> permissions)
    {
        _pluginId = plugin.Id;
        _name = plugin.Name;
        _version = plugin.Version;
        _author = plugin.Author;
        _description = plugin.Description;
        _isEnabled = plugin.Enabled;
        _permissionMode = plugin.PermissionMode;
        _installedAt = plugin.InstalledAt;
        _assemblyPath = plugin.AssemblyPath;

        foreach (var permission in permissions)
        {
            Permissions.Add(permission);
        }
    }
}
