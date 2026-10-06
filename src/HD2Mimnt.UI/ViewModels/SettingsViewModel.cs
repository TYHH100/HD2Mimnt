using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly GameDetectionService _gameDetectionService;
    private readonly NotificationService _notificationService;
    private readonly IImageCacheService _imageCacheService;

    [ObservableProperty] private string _gamePath = string.Empty;
    [ObservableProperty] private bool _autoDetectGame = true;
    [ObservableProperty] private DeploymentMethod _defaultDeploymentMethod = DeploymentMethod.Copy;
    [ObservableProperty] private bool _checkBeforeDeploy = true;
    [ObservableProperty] private bool _showThumbnails = true;
    [ObservableProperty] private ModFilterType _defaultFilterType = ModFilterType.All;
    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _listDensity = "Comfortable";
    [ObservableProperty] private string _backgroundImagePath = string.Empty;
    [ObservableProperty] private double _backgroundImageOpacity = 25;
    [ObservableProperty] private double _backgroundBlur = 0;
    [ObservableProperty] private double _cardOpacity = 100;
    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private bool _autoSwitchProfileOnSelect = true;
    [ObservableProperty] private string _selectedCategory = "游戏";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _cacheStatus = "缓存信息未加载";

    public IEnumerable<DeploymentMethod> DeploymentMethods => Enum.GetValues<DeploymentMethod>();
    public IEnumerable<ModFilterType> FilterTypes => Enum.GetValues<ModFilterType>();
    public IEnumerable<string> ThemeOptions { get; } = new[] { "Dark", "Light", "System" };
    public IEnumerable<string> ListDensityOptions { get; } = new[] { "Comfortable", "Compact" };
    public IEnumerable<string> SettingsCategories { get; } = new[] { "游戏", "配置文件", "部署", "模组库", "外观", "缓存" };
    public event EventHandler<string>? ThemeChanged;
    public event EventHandler<string>? ListDensityChanged;
    public event EventHandler? LibraryDefaultsChanged;
    public event EventHandler? AppearanceChanged;

    public bool IsGameCategoryVisible => SelectedCategory == "游戏";
    public bool IsProfileCategoryVisible => SelectedCategory == "配置文件";
    public bool IsDeploymentCategoryVisible => SelectedCategory == "部署";
    public bool IsLibraryCategoryVisible => SelectedCategory == "模组库";
    public bool IsAppearanceCategoryVisible => SelectedCategory == "外观";
    public bool IsCacheCategoryVisible => SelectedCategory == "缓存";

    public SettingsViewModel(SettingsService settingsService, GameDetectionService gameDetectionService, NotificationService notificationService, IImageCacheService imageCacheService)
    {
        _settingsService = settingsService;
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _imageCacheService = imageCacheService;
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsGameCategoryVisible));
        OnPropertyChanged(nameof(IsProfileCategoryVisible));
        OnPropertyChanged(nameof(IsDeploymentCategoryVisible));
        OnPropertyChanged(nameof(IsLibraryCategoryVisible));
        OnPropertyChanged(nameof(IsAppearanceCategoryVisible));
        OnPropertyChanged(nameof(IsCacheCategoryVisible));
    }

    partial void OnThemeChanged(string value) => ThemeChanged?.Invoke(this, value);

    partial void OnListDensityChanged(string value) => ListDensityChanged?.Invoke(this, value);

    partial void OnDefaultFilterTypeChanged(ModFilterType value) => LibraryDefaultsChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            GamePath = await _settingsService.GetSettingAsync(SettingKeys.GamePath, string.Empty);
            AutoDetectGame = await _settingsService.GetSettingAsync(SettingKeys.GameAutoDetect, true);
            AutoSwitchProfileOnSelect = await _settingsService.GetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
            var method = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentMethod, DeploymentMethod.Copy.ToString());
            DefaultDeploymentMethod = Enum.TryParse<DeploymentMethod>(method, out var parsed) ? parsed : DeploymentMethod.Copy;
            CheckBeforeDeploy = await _settingsService.GetSettingAsync(SettingKeys.DeploymentCheckBeforeDeploy, true);
            ShowThumbnails = await _settingsService.GetSettingAsync(SettingKeys.ShowModThumbnails, true);
            var filter = await _settingsService.GetSettingAsync(SettingKeys.DefaultFilterCategory, ModFilterType.All.ToString());
            DefaultFilterType = Enum.TryParse<ModFilterType>(filter, out var parsedFilter) ? parsedFilter : ModFilterType.All;
            Theme = await _settingsService.GetSettingAsync(SettingKeys.Theme, "Dark");
            ListDensity = await _settingsService.GetSettingAsync(SettingKeys.ListDensity, "Comfortable");
            BackgroundImagePath = await _settingsService.GetSettingAsync(SettingKeys.BackgroundImagePath, string.Empty);
            BackgroundImageOpacity = await _settingsService.GetSettingAsync(SettingKeys.BackgroundImageOpacity, 25.0);
            BackgroundBlur = await _settingsService.GetSettingAsync(SettingKeys.BackgroundBlur, 0.0);
            CardOpacity = await _settingsService.GetSettingAsync(SettingKeys.CardOpacity, 100.0);
            EnableAnimations = await _settingsService.GetSettingAsync(SettingKeys.EnableAnimations, true);
            RefreshCacheStatus();
            StatusMessage = "设置已加载";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var gamePath = GamePath.Trim();
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                await _settingsService.SetSettingAsync(SettingKeys.GamePath, string.Empty);
                if (AutoDetectGame)
                {
                    await _gameDetectionService.AutoDetectAsync();
                }
                else
                {
                    await _gameDetectionService.ApplyConfiguredPathAsync(string.Empty);
                }
                GamePath = _gameDetectionService.CurrentGameInfo?.Path ?? string.Empty;
            }
            else
            {
                await _gameDetectionService.SetGamePathAsync(gamePath);
                GamePath = gamePath;
            }
            await _settingsService.SetSettingAsync(SettingKeys.GameAutoDetect, AutoDetectGame);
            await _settingsService.SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, AutoSwitchProfileOnSelect);
            await _settingsService.SetSettingAsync(SettingKeys.DefaultDeploymentMethod, DefaultDeploymentMethod.ToString());
            await _settingsService.SetSettingAsync(SettingKeys.DeploymentCheckBeforeDeploy, CheckBeforeDeploy);
            await _settingsService.SetSettingAsync(SettingKeys.ShowModThumbnails, ShowThumbnails);
            await _settingsService.SetSettingAsync(SettingKeys.DefaultFilterCategory, DefaultFilterType.ToString());
            await _settingsService.SetSettingAsync(SettingKeys.Theme, Theme);
            await _settingsService.SetSettingAsync(SettingKeys.ListDensity, ListDensity);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundImagePath, BackgroundImagePath);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundImageOpacity, BackgroundImageOpacity);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundBlur, BackgroundBlur);
            await _settingsService.SetSettingAsync(SettingKeys.CardOpacity, CardOpacity);
            await _settingsService.SetSettingAsync(SettingKeys.EnableAnimations, EnableAnimations);
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "设置已保存";
            _notificationService.ShowSuccess("设置已保存", "设置");
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存设置失败：{ex.Message}";
            _notificationService.ShowError($"保存设置失败: {ex.Message}", "设置");
        }
    }

    [RelayCommand]
    private async Task DetectGameAsync()
    {
        // "自动识别"是显式触发，即使已配置路径也重新扫描
        await _gameDetectionService.AutoDetectAsync(scanEvenIfConfigured: true);
        GamePath = _gameDetectionService.CurrentGameInfo?.Path ?? string.Empty;
        StatusMessage = _gameDetectionService.CurrentGameInfo?.Status.ToString() ?? "未检测到游戏";
    }

    [RelayCommand]
    private async Task ValidateGameAsync()
    {
        var valid = await _gameDetectionService.ValidateGameDirectoryAsync(GamePath);
        StatusMessage = valid ? "游戏目录有效" : "游戏目录无效";
        if (!valid)
        {
            _notificationService.ShowWithNavigation(
                NotificationLevel.Warning,
                "未找到有效的 Helldivers 2 游戏目录",
                "打开设置",
                NotificationNavigationTarget.Settings,
                "游戏设置");
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        await _settingsService.ResetToDefaultAsync();
        await LoadAsync();
        _notificationService.ShowInfo("设置已恢复默认值", "设置");
    }

    /// <summary>外观选图（View 代码后置用系统文件选择器拿到路径后调用）；立即持久化并通知应用。</summary>
    public async Task SetBackgroundImagePathAsync(string path)
    {
        BackgroundImagePath = path;
        await _settingsService.SetSettingAsync(SettingKeys.BackgroundImagePath, path);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage = "背景图片已更新";
    }

    [RelayCommand]
    private async Task ClearBackgroundImageAsync()
    {
        BackgroundImagePath = string.Empty;
        await _settingsService.SetSettingAsync(SettingKeys.BackgroundImagePath, string.Empty);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ClearImageCacheAsync()
    {
        try
        {
            await _imageCacheService.ClearAllCachesAsync();
            RefreshCacheStatus();
            StatusMessage = "图片缓存已清理";
            _notificationService.ShowSuccess("图片缓存已清理", "缓存");
        }
        catch (Exception ex)
        {
            StatusMessage = $"缓存清理失败: {ex.Message}";
            _notificationService.ShowError(StatusMessage, "缓存");
        }
    }

    [RelayCommand]
    private void RefreshCache() => RefreshCacheStatus();

    private void RefreshCacheStatus()
    {
        var stats = _imageCacheService.GetStats();
        CacheStatus = $"内存 {stats.MemoryCacheCount} 项，磁盘 {stats.DiskCacheCount} 项，磁盘占用 {FormatBytes(stats.DiskCacheSizeBytes)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes / (1024d * 1024):0.##} MB";
    }
}
