using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Core.Localization;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly GameDetectionService _gameDetectionService;
    private readonly NotificationService _notificationService;
    private readonly IImageCacheService _imageCacheService;
    private readonly DownloadSettingsService _downloadSettingsService;
    private readonly StorageSettingsService? _storageSettings;
    private readonly UiLocalization? _localization;
    private readonly IConfirmationService? _confirmationService;
    private bool _syncingLanguage;
    private readonly SemaphoreSlim _languageGate = new(1, 1);
    private int _pendingLanguageChanges;

    [ObservableProperty] private string _selectedLanguageCode = LanguageCodes.Automatic;
    [ObservableProperty] private bool _isChangingLanguage;
    public Task LanguageChangeTask { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<LanguageInfo> LanguageOptions { get; }
    public string CurrentLanguageText
    {
        get
        {
            var language = _localization?.Service?.CurrentLanguage ?? LanguageCodes.SimplifiedChinese;
            var name = LanguageOptions.FirstOrDefault(option => option.Code == language)?.NativeName ?? language;
            return (_localization ?? UiLocalization.Current).Format("Settings.Language.Current", name);
        }
    }

    [ObservableProperty] private string _gamePath = string.Empty;
    [ObservableProperty] private bool _autoDetectGame = true;
    [ObservableProperty] private DeploymentMethod _defaultDeploymentMethod = DeploymentMethod.Copy;
    [ObservableProperty] private bool _deployBottomToTop;
    [ObservableProperty] private bool _checkBeforeDeploy = true;
    [ObservableProperty] private bool _showThumbnails = true;
    [ObservableProperty] private bool _searchFuzzy;
    [ObservableProperty] private bool _searchPinyin;
    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _listDensity = "Comfortable";
    [ObservableProperty] private string _backgroundImagePath = string.Empty;
    [ObservableProperty] private double _backgroundImageOpacity = 25;
    [ObservableProperty] private double _backgroundBlur = 0;
    [ObservableProperty] private double _cardOpacity = 100;
    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private bool _autoSwitchProfileOnSelect = true;
    [ObservableProperty] private bool _autoCheckConflicts;
    [ObservableProperty] private bool _lockLoaders = true;
    [ObservableProperty] private bool _autoAddImportedMods;
    [ObservableProperty] private bool _importAutoClose;
    [ObservableProperty] private bool _keepModVersions;
    [ObservableProperty] private bool _allowDuplicateImports;
    [ObservableProperty] private bool _compatibilityCheckOnStartup;
    [ObservableProperty] private bool _compatibilityEnableBatchRepair;
    [ObservableProperty] private string _selectedCategory = "游戏";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _cacheStatus = "缓存信息未加载";
    [ObservableProperty] private int _downloadParallelSegments = 4;
    [ObservableProperty] private int _downloadSegmentSizeMiB = 4;
    [ObservableProperty] private int _downloadMaxRetries = 2;
    [ObservableProperty] private int _downloadRetryDelayMs = 500;
    [ObservableProperty] private bool _downloadEnableResume = true;
    [ObservableProperty] private string _appDataPath = string.Empty;
    [ObservableProperty] private string _modsStoragePath = string.Empty;
    [ObservableProperty] private string _tempCachePath = string.Empty;
    [ObservableProperty] private bool _storageRestartRequired;
    public string ActiveStoragePaths => _storageSettings == null ? string.Empty :
        (_localization ?? UiLocalization.Current).Format("Settings.Storage.ActivePaths",
            _storageSettings.Current.AppDataPath, _storageSettings.Current.ModsPath, _storageSettings.Current.CachePath);

    public IEnumerable<DeploymentMethod> DeploymentMethods => Enum.GetValues<DeploymentMethod>();
    public IEnumerable<string> ThemeOptions { get; } = new[] { "Dark", "Light", "System" };
    public IEnumerable<string> ListDensityOptions { get; } = new[] { "Comfortable", "Compact" };
    public NexusModsAccountViewModel? NexusAccount { get; }
    public AyakaModsAccountViewModel? AyakaAccount { get; }
    public event EventHandler<string>? ThemeChanged;
    public event EventHandler<string>? ListDensityChanged;
    public event EventHandler? AppearanceChanged;
    public event EventHandler<bool>? AutoCheckConflictsChanged;

    public bool IsGameCategoryVisible => SelectedCategory == "游戏";
    public bool IsProfileCategoryVisible => SelectedCategory == "配置文件";
    public bool IsDeploymentCategoryVisible => SelectedCategory == "部署";
    public bool IsLibraryCategoryVisible => SelectedCategory == "模组库";
    public bool IsSearchCategoryVisible => SelectedCategory == "搜索";
    public bool IsCompatibilityCategoryVisible => SelectedCategory == "兼容性";
    public bool IsAppearanceCategoryVisible => SelectedCategory == "外观";
    public bool IsCacheCategoryVisible => SelectedCategory == "缓存";
    public bool IsDownloadCategoryVisible => SelectedCategory == "下载";
    public bool IsStorageCategoryVisible => SelectedCategory == "存储位置";
    public bool IsAccountCategoryVisible => SelectedCategory == "账号";

    public SettingsViewModel(SettingsService settingsService, GameDetectionService gameDetectionService, NotificationService notificationService, IImageCacheService imageCacheService,
        UiLocalization? localization = null, StorageSettingsService? storageSettings = null,
        NexusModsAccountViewModel? nexusAccount = null, AyakaModsAccountViewModel? ayakaAccount = null,
        IConfirmationService? confirmationService = null, ModCompatibilityService? compatibilityService = null)
    {
        _settingsService = settingsService;
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _imageCacheService = imageCacheService;
        _downloadSettingsService = new DownloadSettingsService(settingsService);
        _storageSettings = storageSettings;
        _localization = localization;
        _confirmationService = confirmationService;
        _compatibilityService = compatibilityService;
        NexusAccount = nexusAccount;
        AyakaAccount = ayakaAccount;
        LanguageOptions = new[] { new LanguageInfo(LanguageCodes.Automatic, "跟随系统") }
            .Concat(localization?.Service?.SupportedLanguages ?? new HD2Mimnt.Application.Localization.TranslationCatalog().SupportedLanguages)
            .ToArray();
        if (_localization != null) _localization.PropertyChanged += OnUiLanguageChanged;
        SynchronizeLanguage();
        LoadEntryVisibility();
        LoadLogSettings();
    }

    partial void OnSelectedLanguageCodeChanged(string value)
    {
        // ComboBox 初始化/重建选中项时可能短暂回写 null；这不是用户的语言选择。
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!_syncingLanguage && _localization?.Service != null)
            LanguageChangeTask = ApplyLanguageAsync(value);
    }

    private async Task ApplyLanguageAsync(string code)
    {
        _pendingLanguageChanges++;
        IsChangingLanguage = true;
        await _languageGate.WaitAsync();
        try
        {
            await _localization!.Service!.SetLanguageAsync(code);
        }
        catch (Exception ex)
        {
            StatusMessage = _localization!.Format("Settings.Language.ChangeFailed", ex.Message);
            _notificationService.ShowError(StatusMessage, _localization["Settings.Title"]);
        }
        finally
        {
            _languageGate.Release();
            _pendingLanguageChanges--;
            IsChangingLanguage = _pendingLanguageChanges > 0;
            if (!IsChangingLanguage) SynchronizeLanguage();
        }
    }

    private void OnUiLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        if (!IsChangingLanguage) SynchronizeLanguage();
        OnPropertyChanged(nameof(CurrentLanguageText));
        OnPropertyChanged(nameof(ActiveStoragePaths));
        OnPropertyChanged(nameof(CompatibilityDataStatusText));
    }

    private void SynchronizeLanguage()
    {
        _syncingLanguage = true;
        try { SelectedLanguageCode = _localization?.Service?.SelectedLanguage ?? LanguageCodes.Automatic; }
        finally { _syncingLanguage = false; }
        OnPropertyChanged(nameof(CurrentLanguageText));
    }

    public void Dispose()
    {
        _compatibilityDataOperation?.Cancel();
        if (_localization != null) _localization.PropertyChanged -= OnUiLanguageChanged;
        NexusAccount?.Dispose();
    }

    public async Task StopAccountsAsync()
    {
        var nexus = NexusAccount?.StopAsync() ?? Task.CompletedTask;
        var ayaka = AyakaAccount?.StopAsync() ?? Task.CompletedTask;
        await Task.WhenAll(nexus, ayaka);
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsLogCategoryVisible));
        if (value != "账号") _ = StopAccountsAsync();
        OnPropertyChanged(nameof(IsGameCategoryVisible));
        OnPropertyChanged(nameof(IsProfileCategoryVisible));
        OnPropertyChanged(nameof(IsDeploymentCategoryVisible));
        OnPropertyChanged(nameof(IsLibraryCategoryVisible));
        OnPropertyChanged(nameof(IsSearchCategoryVisible));
        OnPropertyChanged(nameof(IsCompatibilityCategoryVisible));
        OnPropertyChanged(nameof(IsAppearanceCategoryVisible));
        OnPropertyChanged(nameof(IsCacheCategoryVisible));
        OnPropertyChanged(nameof(IsDownloadCategoryVisible));
        OnPropertyChanged(nameof(IsStorageCategoryVisible));
        OnPropertyChanged(nameof(IsAccountCategoryVisible));
        OnPropertyChanged(nameof(IsEntriesCategoryVisible));
        if (value == "兼容性") _ = UpdateStaleCompatibilityDataAsync();
    }

    partial void OnThemeChanged(string value) => ThemeChanged?.Invoke(this, value);

    partial void OnListDensityChanged(string value) => ListDensityChanged?.Invoke(this, value);


    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            SynchronizeLanguage();
            LoadEntryVisibility();
            LoadStoragePaths();
            LoadLogSettings();
            GamePath = await _settingsService.GetSettingAsync(SettingKeys.GamePath, string.Empty);
            AutoDetectGame = await _settingsService.GetSettingAsync(SettingKeys.GameAutoDetect, true);
            AutoSwitchProfileOnSelect = await _settingsService.GetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, true);
            AutoCheckConflicts = await _settingsService.GetSettingAsync(SettingKeys.ProfilesAutoCheckConflicts, false);
            LockLoaders = await _settingsService.GetSettingAsync(SettingKeys.ProfilesLockLoaders, true);
            AutoAddImportedMods = await _settingsService.GetSettingAsync(SettingKeys.ProfilesAutoAddImportedMods, false);
            ImportAutoClose = await _settingsService.GetSettingAsync(SettingKeys.ImportAutoClose, false);
            KeepModVersions = await _settingsService.GetSettingAsync(SettingKeys.KeepModVersions, false);
            AllowDuplicateImports = await _settingsService.GetSettingAsync(SettingKeys.AllowDuplicateImports, false);
            LoadCompatibilitySettings();
            AutoCheckConflictsChanged?.Invoke(this, AutoCheckConflicts);
            var method = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentMethod, DeploymentMethod.Copy.ToString());
            DefaultDeploymentMethod = Enum.TryParse<DeploymentMethod>(method, out var parsed) ? parsed : DeploymentMethod.Copy;
            DeployBottomToTop = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString())
                == DeploymentOrder.BottomToTop.ToString();
            CheckBeforeDeploy = await _settingsService.GetSettingAsync(SettingKeys.DeploymentCheckBeforeDeploy, true);
            ShowThumbnails = await _settingsService.GetSettingAsync(SettingKeys.ShowModThumbnails, true);
            SearchFuzzy = await _settingsService.GetSettingAsync(SettingKeys.SearchFuzzy, false);
            SearchPinyin = await _settingsService.GetSettingAsync(SettingKeys.SearchPinyin, false);
            Theme = await _settingsService.GetSettingAsync(SettingKeys.Theme, "Dark");
            ListDensity = await _settingsService.GetSettingAsync(SettingKeys.ListDensity, "Comfortable");
            BackgroundImagePath = await _settingsService.GetSettingAsync(SettingKeys.BackgroundImagePath, string.Empty);
            BackgroundImageOpacity = await _settingsService.GetSettingAsync(SettingKeys.BackgroundImageOpacity, 25.0);
            BackgroundBlur = await _settingsService.GetSettingAsync(SettingKeys.BackgroundBlur, 0.0);
            CardOpacity = await _settingsService.GetSettingAsync(SettingKeys.CardOpacity, 100.0);
            EnableAnimations = await _settingsService.GetSettingAsync(SettingKeys.EnableAnimations, true);
            var downloads = await _downloadSettingsService.GetOptionsAsync();
            DownloadParallelSegments = downloads.MaxParallelSegments;
            DownloadSegmentSizeMiB = (int)(downloads.MinimumSegmentSize / (1024 * 1024));
            DownloadMaxRetries = downloads.MaxRetries;
            DownloadRetryDelayMs = (int)downloads.RetryDelay.TotalMilliseconds;
            DownloadEnableResume = downloads.EnableResume;
            RefreshCacheStatus();
            StatusMessage = "设置已加载";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLogCategoryVisible)
        {
            await SaveLogSettingsAsync();
            return;
        }
        try
        {
            if (IsEntriesCategoryVisible)
            {
                await SaveEntryVisibilityAsync();
                StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Entries.Saved"];
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            if (IsCompatibilityCategoryVisible)
            {
                await _settingsService.SetSettingsAsync(new Dictionary<string, object>
                {
                    [SettingKeys.CompatibilityCheckOnStartup] = CompatibilityCheckOnStartup,
                    [SettingKeys.CompatibilityEnableBatchRepair] = CompatibilityEnableBatchRepair
                });
                StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Compatibility.Saved"];
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            if (IsStorageCategoryVisible)
            {
                if (_storageSettings == null) throw new InvalidOperationException("存储设置服务未初始化。");
                IsLoading = true;
                await _storageSettings.SaveAsync(new(AppDataPath, ModsStoragePath, TempCachePath));
                LoadStoragePaths();
                StatusMessage = (_localization ?? UiLocalization.Current)[StorageRestartRequired
                    ? "Settings.Storage.Saved" : "Settings.Storage.Unchanged"];
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            var downloads = new DownloadOptions
            {
                MaxParallelSegments = DownloadParallelSegments,
                MinimumSegmentSize = DownloadSegmentSizeMiB * 1024L * 1024,
                MaxRetries = DownloadMaxRetries,
                RetryDelay = TimeSpan.FromMilliseconds(DownloadRetryDelayMs),
                EnableResume = DownloadEnableResume
            };
            downloads.Validate();
            if (DownloadSegmentSizeMiB is < 1 or > 256)
                throw new ArgumentOutOfRangeException(nameof(DownloadSegmentSizeMiB), "分段大小应为 1–256 MiB。");
            // Download settings can be saved independently of game path validation.
            if (IsDownloadCategoryVisible)
            {
                await _downloadSettingsService.SaveAsync(downloads);
                StatusMessage = "下载设置已保存，下次下载生效";
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            // 配置偏好可独立保存，不受游戏路径验证影响。
            if (IsProfileCategoryVisible)
            {
                await SaveProfilePreferencesAsync();
                StatusMessage = "配置文件设置已保存";
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            if (IsLibraryCategoryVisible)
            {
                await SaveLibraryPreferencesAsync();
                StatusMessage = "设置已保存";
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
            if (IsSearchCategoryVisible)
            {
                await SaveSearchPreferencesAsync();
                StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Search.Saved"];
                _notificationService.ShowSuccess(StatusMessage, "设置");
                return;
            }
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
            await SaveProfilePreferencesAsync();
            await _settingsService.SetSettingAsync(SettingKeys.DefaultDeploymentMethod, DefaultDeploymentMethod.ToString());
            await _settingsService.SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
                (DeployBottomToTop ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom).ToString());
            await _settingsService.SetSettingAsync(SettingKeys.DeploymentCheckBeforeDeploy, CheckBeforeDeploy);
            await SaveLibraryPreferencesAsync();
            await _settingsService.SetSettingAsync(SettingKeys.Theme, Theme);
            await _settingsService.SetSettingAsync(SettingKeys.ListDensity, ListDensity);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundImagePath, BackgroundImagePath);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundImageOpacity, BackgroundImageOpacity);
            await _settingsService.SetSettingAsync(SettingKeys.BackgroundBlur, BackgroundBlur);
            await _settingsService.SetSettingAsync(SettingKeys.CardOpacity, CardOpacity);
            await _settingsService.SetSettingAsync(SettingKeys.EnableAnimations, EnableAnimations);
            await _downloadSettingsService.SaveAsync(downloads);
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "设置已保存";
            _notificationService.ShowSuccess("设置已保存", "设置");
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存设置失败：{ex.Message}";
            _notificationService.ShowError($"保存设置失败: {ex.Message}", "设置");
        }
        finally { IsLoading = false; }
    }

    private async Task SaveLibraryPreferencesAsync()
    {
        await _settingsService.SetSettingAsync(SettingKeys.ShowModThumbnails, ShowThumbnails);
        await _settingsService.SetSettingAsync(SettingKeys.ImportAutoClose, ImportAutoClose);
        await _settingsService.SetSettingAsync(SettingKeys.KeepModVersions, KeepModVersions);
        await _settingsService.SetSettingAsync(SettingKeys.AllowDuplicateImports, AllowDuplicateImports);
    }

    private async Task SaveSearchPreferencesAsync()
    {
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.SearchFuzzy] = SearchFuzzy,
            [SettingKeys.SearchPinyin] = SearchPinyin
        });
    }

    [RelayCommand]
    private async Task ToggleKeepModVersionsAsync()
    {
        // 切换控件会先切换自身状态；等待确认期间立即恢复已确认的值。
        OnPropertyChanged(nameof(KeepModVersions));
        if (!KeepModVersions && (_confirmationService == null || !await _confirmationService.ConfirmAsync(
                "保留模组历史版本", "开启后，使用压缩包或在线更新模组会保留之前的完整文件，可在模组详情中切换本地版本。每个版本都会额外占用磁盘空间，更新越多占用越大，且不会自动清理。切换后需检查模组选项并重新部署。关闭只停止保留新版本，已有版本仍可切换或手动删除。确定开启吗？")))
        {
            OnPropertyChanged(nameof(KeepModVersions));
            return;
        }
        KeepModVersions = !KeepModVersions;
    }

    private async Task SaveProfilePreferencesAsync()
    {
        await _settingsService.SetSettingAsync(SettingKeys.ProfilesAutoAddImportedMods, AutoAddImportedMods);
        await _settingsService.SetSettingAsync(SettingKeys.ProfilesLockLoaders, LockLoaders);
        await _settingsService.SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, AutoSwitchProfileOnSelect);
        await _settingsService.SetSettingAsync(SettingKeys.ProfilesAutoCheckConflicts, AutoCheckConflicts);
        AutoCheckConflictsChanged?.Invoke(this, AutoCheckConflicts);
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
        if (IsCompatibilityCategoryVisible)
        {
            await _settingsService.ResetCategoryAsync("Compatibility");
            LoadCompatibilitySettings();
            StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Compatibility.Reset"];
            return;
        }
        if (IsLogCategoryVisible)
        {
            await _settingsService.ResetCategoryAsync("Diagnostics");
            LoadLogSettings();
            StatusMessage = "日志设置已恢复默认值";
            return;
        }
        if (IsEntriesCategoryVisible)
        {
            await _settingsService.SetSettingAsync(SettingKeys.HiddenEntries, Array.Empty<string>());
            LoadEntryVisibility();
            StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Entries.Reset"];
            return;
        }
        if (IsStorageCategoryVisible && _storageSettings != null)
        {
            AppDataPath = _storageSettings.Defaults.AppDataPath;
            ModsStoragePath = _storageSettings.Defaults.ModsPath;
            TempCachePath = _storageSettings.Defaults.CachePath;
            StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Storage.Defaults"];
            return;
        }
        if (IsDownloadCategoryVisible)
        {
            await _settingsService.ResetCategoryAsync("Download");
            await LoadAsync();
            StatusMessage = "下载设置已恢复默认值";
            _notificationService.ShowInfo(StatusMessage, "设置");
            return;
        }
        if (IsSearchCategoryVisible)
        {
            await _settingsService.ResetCategoryAsync("Search");
            SearchFuzzy = false;
            SearchPinyin = false;
            StatusMessage = (_localization ?? UiLocalization.Current)["Settings.Search.Reset"];
            _notificationService.ShowInfo(StatusMessage, "设置");
            return;
        }
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

    private void LoadStoragePaths()
    {
        if (_storageSettings == null) return;
        AppDataPath = _storageSettings.Selected.AppDataPath;
        ModsStoragePath = _storageSettings.Selected.ModsPath;
        TempCachePath = _storageSettings.Selected.CachePath;
        StorageRestartRequired = _storageSettings.RestartRequired;
        OnPropertyChanged(nameof(ActiveStoragePaths));
    }

    private void LoadCompatibilitySettings()
    {
        CompatibilityCheckOnStartup = _settingsService.GetSetting(SettingKeys.CompatibilityCheckOnStartup, false);
        CompatibilityEnableBatchRepair = _settingsService.GetSetting(SettingKeys.CompatibilityEnableBatchRepair, false);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes / (1024d * 1024):0.##} MB";
    }
}
