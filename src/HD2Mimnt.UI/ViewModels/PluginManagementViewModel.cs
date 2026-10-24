using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.ObjectModel;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// ViewModel for Plugin Management page in Settings
/// </summary>
public partial class PluginManagementViewModel : ObservableObject
{
    private readonly IPluginService _pluginService;
    private readonly IConfirmationService _confirmationService;

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

    [ObservableProperty]
    private ObservableCollection<PluginExecutionHistory> _executionHistory = new();

    [ObservableProperty]
    private PluginItemViewModel? _historyPlugin;

    [ObservableProperty]
    private bool _isHistoryOpen;

    public event EventHandler? InstallManifestRequested;
    public event EventHandler<PluginItemViewModel>? PermissionEditorRequested;

    public PluginManagementViewModel(IPluginService pluginService)
        : this(pluginService, new TestConfirmationService())
    {
    }

    public PluginManagementViewModel(IPluginService pluginService, IConfirmationService confirmationService)
    {
        _pluginService = pluginService;
        _confirmationService = confirmationService;
    }

    public async Task InitializeAsync()
    {
        await LoadPluginsAsync();
    }

    [RelayCommand]
    private async Task LoadPluginsAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载插件...";

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
            StatusMessage = $"已加载 {InstalledPlugins.Count} 个插件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载插件失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void InstallPlugin()
    {
        InstallManifestRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void EditPermissions(PluginItemViewModel? plugin)
    {
        if (plugin != null) PermissionEditorRequested?.Invoke(this, plugin);
    }

    public async Task ApplyPermissionsAsync(PluginItemViewModel plugin, IEnumerable<PermissionItemViewModel> items)
    {
        foreach (var item in items)
        {
            var result = item.IsGranted
                ? await _pluginService.GrantPermissionAsync(plugin.PluginId, item.Permission)
                : await _pluginService.RevokePermissionAsync(plugin.PluginId, item.Permission);
            if (!result.Success)
            {
                StatusMessage = $"权限更新失败：{result.Error ?? result.Message}";
                return;
            }
        }

        await RefreshPermissionsAsync(plugin);
        StatusMessage = $"已更新 {plugin.Name} 的权限";
    }

    public async Task RefreshPermissionsAsync(PluginItemViewModel plugin)
    {
        var permissions = await _pluginService.GetPluginPermissionsAsync(plugin.PluginId);
        plugin.ReplacePermissions(permissions);
    }

    public async Task InstallPluginAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "正在安装插件...";
        try
        {
            var result = await _pluginService.InstallPluginAsync(manifestPath, cancellationToken);
            if (result.Success)
            {
                await LoadPluginsAsync();
                StatusMessage = "插件安装成功";
            }
            else
            {
                StatusMessage = $"插件安装失败：{result.Error ?? result.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"插件安装失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task<PluginManifestPreview?> GetManifestPreviewAsync(string manifestPath, CancellationToken cancellationToken = default) =>
        _pluginService.GetManifestPreviewAsync(manifestPath, cancellationToken);

    [RelayCommand]
    private async Task EnablePluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;

        IsLoading = true;
        StatusMessage = $"正在启用 {plugin.Name}...";

        try
        {
            var result = await _pluginService.EnablePluginAsync(plugin.PluginId);
            if (result.Success)
            {
                plugin.IsEnabled = true;
                UpdateStatistics();
                StatusMessage = $"已启用 {plugin.Name}";
            }
            else
            {
                StatusMessage = $"启用 {plugin.Name} 失败：{result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
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
        StatusMessage = $"正在停用 {plugin.Name}...";

        try
        {
            var result = await _pluginService.DisablePluginAsync(plugin.PluginId);
            if (result.Success)
            {
                plugin.IsEnabled = false;
                UpdateStatistics();
                StatusMessage = $"已停用 {plugin.Name}";
            }
            else
            {
                StatusMessage = $"停用 {plugin.Name} 失败：{result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
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

        if (!await _confirmationService.ConfirmAsync(
                "卸载插件",
                $"确定卸载“{plugin.Name}”吗？插件将停止运行并从已安装列表移除。"))
        {
            return;
        }

        IsLoading = true;
        StatusMessage = $"正在卸载 {plugin.Name}...";

        try
        {
            var result = await _pluginService.UninstallPluginAsync(plugin.PluginId);
            if (result.Success)
            {
                InstalledPlugins.Remove(plugin);
                UpdateStatistics();
                StatusMessage = $"已卸载 {plugin.Name}";
            }
            else
            {
                StatusMessage = $"卸载 {plugin.Name} 失败：{result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
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
            HistoryPlugin = plugin;
            ExecutionHistory = new ObservableCollection<PluginExecutionHistory>(history);
            IsHistoryOpen = true;
            StatusMessage = $"已找到 {plugin.Name} 的 {history.Count} 条运行记录";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取记录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadPluginsAsync();
    }

    [RelayCommand]
    private void CloseHistory()
    {
        IsHistoryOpen = false;
        HistoryPlugin = null;
        ExecutionHistory.Clear();
    }

    private void UpdateStatistics()
    {
        TotalPlugins = InstalledPlugins.Count;
        EnabledPlugins = InstalledPlugins.Count(p => p.IsEnabled);
        DisabledPlugins = InstalledPlugins.Count(p => !p.IsEnabled);
    }

    private sealed class TestConfirmationService : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
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

    public string StatusText => IsEnabled ? "已启用" : "已停用";

    public string StatusIcon => IsEnabled ? "启" : "停";

    public string PermissionModeIcon => PermissionMode == "FullAccess" ? "全" : "沙";

    public string PermissionModeDisplay => PermissionMode == "FullAccess" ? "Full Access" : "Sandbox";

    public int GrantedPermissionsCount => Permissions.Count(p => p.Granted);

    public PluginItemViewModel(Plugin plugin, List<PluginPermission> permissions)
    {
        _pluginId = plugin.Id;
        _name = plugin.Name;
        _version = plugin.Version;
        _author = plugin.Author ?? string.Empty;
        _description = plugin.Description ?? string.Empty;
        _isEnabled = plugin.Enabled;
        _permissionMode = plugin.PermissionMode;
        _installedAt = plugin.InstalledAt;
        _assemblyPath = plugin.AssemblyPath;

        foreach (var permission in permissions)
        {
            Permissions.Add(permission);
        }
    }

    public void ReplacePermissions(IEnumerable<PluginPermission> permissions)
    {
        Permissions = new ObservableCollection<PluginPermission>(permissions);
        OnPropertyChanged(nameof(GrantedPermissionsCount));
    }
}
