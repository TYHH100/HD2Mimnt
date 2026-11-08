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

    /// <summary>插件安装/启停/授权/卸载后触发，供主窗口刷新页面与功能菜单入口。</summary>
    public event EventHandler? ContributionsChanged;

    [ObservableProperty]
    private string _activeThemeName = string.Empty;

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

    /// <summary>
    /// 通知主窗口重新读取插件的界面贡献（导航页 / 功能菜单 / 工具栏）。
    /// 启用、停用、卸载、授权变更都会改变可用贡献集合，必须显式通知，
    /// 否则已经停用的插件仍会占着导航项和菜单入口。
    /// </summary>
    private void NotifyContributionsChanged() => ContributionsChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task LoadPluginsAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载插件...";

        try
        {
            var plugins = await _pluginService.GetAllPluginsAsync();
            var uiBundles = await _pluginService.GetUiContributionsAsync() ?? new List<PluginUiBundle>();
            var bundleByPlugin = uiBundles.ToDictionary(b => b.PluginId, StringComparer.Ordinal);

            InstalledPlugins.Clear();
            foreach (var plugin in plugins)
            {
                var permissions = await _pluginService.GetPluginPermissionsAsync(plugin.Id);
                // 模组右键菜单类的命令在这里没有目标模组，只在管理页展示通用命令
                var commands = (await _pluginService.GetPluginCommandsAsync(plugin.Id)
                        ?? new List<PluginCommandInfo>())
                    .Where(c => !HD2Mimnt.PluginSDK.PluginCommandPlacements.IsModContextMenu(c.Placement))
                    .ToList();

                bundleByPlugin.TryGetValue(plugin.Id, out var bundle);
                InstalledPlugins.Add(new PluginItemViewModel(plugin, permissions.ToList(), commands, RunCommandAsync, bundle));
            }

            UpdateStatistics();
            StatusMessage = $"已加载 {InstalledPlugins.Count} 个插件";
            NotifyContributionsChanged();
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
        // 菜单项的可用状态依赖授权结果，撤销授权后必须同步禁用对应入口
        NotifyContributionsChanged();
    }

    public async Task RefreshPermissionsAsync(PluginItemViewModel plugin)
    {
        var permissions = await _pluginService.GetPluginPermissionsAsync(plugin.PluginId);
        plugin.ReplacePermissions(permissions);
    }

    public async Task InstallPluginAsync(
        string manifestPath,
        PluginInstallConsent? consent = null,
        CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "正在安装插件...";
        try
        {
            var result = await _pluginService.InstallPluginAsync(manifestPath, consent, cancellationToken);
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
                NotifyContributionsChanged();
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
                NotifyContributionsChanged();
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
                NotifyContributionsChanged();
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

    /// <summary>
    /// 执行插件声明的命令。宿主未运行时由 PluginService 按需拉起；
    /// 结果写入状态栏，明细同时落在执行历史中。
    /// </summary>
    public async Task RunCommandAsync(PluginCommandItemViewModel? command)
    {
        if (command == null) return;

        IsLoading = true;
        StatusMessage = $"正在执行 {command.DisplayName}...";

        try
        {
            var result = await _pluginService.ExecutePluginCommandAsync(command.PluginId, command.Name);

            if (result.Success)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? $"命令 {command.DisplayName} 已执行"
                    : $"命令 {command.DisplayName} 已执行：{result.Message}";
            }
            else
            {
                StatusMessage = $"命令 {command.DisplayName} 执行失败：{result.Error ?? result.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"命令 {command.DisplayName} 执行失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
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

    /// <summary>应用插件声明的主题包（只覆盖白名单设计令牌）。</summary>
    [RelayCommand]
    private void ApplyTheme(PluginThemeOption? option)
    {
        if (option is null) return;

        if (!option.Bundle.HasPermission("ui.theme"))
        {
            StatusMessage = "该主题需要权限 ui.theme，请在权限设置中授予后再试。";
            return;
        }

        var ignored = PluginThemeService.Apply(option.Bundle, option.Theme);
        ActiveThemeName = $"{option.Bundle.PluginName} · {option.Theme.Name}";
        StatusMessage = ignored.Count == 0
            ? $"已应用主题 {ActiveThemeName}"
            : $"已应用主题 {ActiveThemeName}；已忽略不支持的令牌：{string.Join(", ", ignored)}";
    }

    /// <summary>恢复管理器自带配色。</summary>
    [RelayCommand]
    private void ResetTheme()
    {
        PluginThemeService.Reset();
        ActiveThemeName = string.Empty;
        StatusMessage = "已恢复默认主题";
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

    /// <summary>插件通过 manifest 声明的可执行命令，界面上渲染为按钮。</summary>
    public ObservableCollection<PluginCommandItemViewModel> Commands { get; } = new();

    /// <summary>插件声明的主题包。</summary>
    public ObservableCollection<PluginThemeOption> Themes { get; } = new();

    /// <summary>插件的完整界面声明；没有贡献时为 null。</summary>
    public PluginUiBundle? Bundle { get; }

    public bool HasThemes => Themes.Count > 0;

    public string InstalledAtFormatted => InstalledAt.ToString("yyyy-MM-dd HH:mm");

    public string StatusText => IsEnabled ? "已启用" : "已停用";

    public string StatusIcon => IsEnabled ? "启" : "停";

    public string PermissionModeIcon => PermissionMode == "FullAccess" ? "全" : "沙";

    public string PermissionModeDisplay => PermissionMode == "FullAccess" ? "Full Access" : "Sandbox";

    public int GrantedPermissionsCount => Permissions.Count(p => p.Granted);

    public PluginItemViewModel(Plugin plugin, List<PluginPermission> permissions)
        : this(plugin, permissions, Array.Empty<PluginCommandInfo>(), _ => Task.CompletedTask)
    {
    }

    public PluginItemViewModel(
        Plugin plugin,
        List<PluginPermission> permissions,
        IEnumerable<PluginCommandInfo> commands,
        Func<PluginCommandItemViewModel, Task> executeCommand,
        PluginUiBundle? bundle = null)
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
        Bundle = bundle;

        foreach (var permission in permissions)
        {
            Permissions.Add(permission);
        }

        foreach (var command in commands)
        {
            Commands.Add(new PluginCommandItemViewModel(this, command, executeCommand));
        }

        foreach (var theme in bundle?.Ui.Themes ?? new List<HD2Mimnt.PluginSDK.PluginThemeContribution>())
        {
            if (string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name)) continue;
            Themes.Add(new PluginThemeOption(bundle!, theme));
        }
    }

    public void ReplacePermissions(IEnumerable<PluginPermission> permissions)
    {
        Permissions = new ObservableCollection<PluginPermission>(permissions);
        OnPropertyChanged(nameof(GrantedPermissionsCount));
    }
}

/// <summary>插件声明的主题包，供设置页一键应用。</summary>
public sealed class PluginThemeOption
{
    public PluginThemeOption(PluginUiBundle bundle, HD2Mimnt.PluginSDK.PluginThemeContribution theme)
    {
        Bundle = bundle;
        Theme = theme;
    }

    public PluginUiBundle Bundle { get; }

    public HD2Mimnt.PluginSDK.PluginThemeContribution Theme { get; }

    public string Name => Theme.Name;

    public string? Description => Theme.Description;

    public string ToolTipText => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name}\n{Description}";
}

/// <summary>
/// 插件声明的可执行命令，对应界面上一个按钮。
/// </summary>
public sealed class PluginCommandItemViewModel
{
    public PluginCommandItemViewModel(
        PluginItemViewModel owner,
        PluginCommandInfo command,
        Func<PluginCommandItemViewModel, Task> executeCommand)
    {
        PluginId = owner.PluginId;
        Name = command.Name;
        DisplayName = string.IsNullOrWhiteSpace(command.DisplayName) ? command.Name : command.DisplayName;
        Description = command.Description;
        RequiredPermission = command.RequiredPermission;

        ExecuteCommand = new AsyncRelayCommand(() => executeCommand(this));
    }

    public string PluginId { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string? Description { get; }
    public string? RequiredPermission { get; }

    public IAsyncRelayCommand ExecuteCommand { get; }

    public string ToolTipText => string.IsNullOrWhiteSpace(Description)
        ? DisplayName
        : $"{DisplayName}\n{Description}";
}
