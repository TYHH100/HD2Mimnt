using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideSettingsViewModel : DarktidePageViewModelBase
{
    private readonly DarktideManagerService _manager;
    private readonly IDarktideDialogService _dialogs;
    private readonly DarktideStoragePaths _storage;
    private readonly SettingsService _settings;
    private readonly string _databasePathValue;
    private bool _appearanceLoaded;

    [ObservableProperty] private string _gamePath = "";
    [ObservableProperty] private string _gameStatusText = "";
    [ObservableProperty] private bool _hasValidGame;
    [ObservableProperty] private string _appDataPath = "";
    [ObservableProperty] private string _archivePath = "";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _modsPath = "";
    [ObservableProperty] private string _loaderBackupsPath = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _selectedThemeValue = "Dark";
    [ObservableProperty] private double _cardOpacity = 100;
    [ObservableProperty] private double _cornerRadius = 8;
    [ObservableProperty] private string _backgroundImagePath = "";
    [ObservableProperty] private double _backgroundImageOpacity = 25;
    [ObservableProperty] private double _backgroundBlur = 0;

    /// <summary>取值直接是 Dark/Light/System，由 UiLocalization.OptionKeys 翻成界面文案。</summary>
    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Dark", "Light", "System" };

    public event EventHandler? GamePathChanged;

    /// <summary>外观任一项变化都通知主窗口重算背景与表面透明度。</summary>
    public event EventHandler? AppearanceChanged;

    public DarktideSettingsViewModel(DarktideManagerService manager, IDarktideDialogService dialogs,
        DarktideStoragePaths storage, StorageSettingsService storageSettings, SettingsService settings,
        NotificationService notifications)
        : base(notifications)
    {
        _manager = manager;
        _dialogs = dialogs;
        _storage = storage;
        _settings = settings;
        _databasePathValue = storageSettings.Current.DatabasePath;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RunAsync(LoadAsync, failTitleKey: "Darktide.Settings.Title");

    private Task LoadAsync(CancellationToken cancellationToken)
    {
        GamePath = _manager.GamePath;
        var state = _manager.GetGameState();
        HasValidGame = state.RootPath != null && state.Status != DarktideGameStatus.InvalidPath;
        GameStatusText = state.Status switch
        {
            DarktideGameStatus.LoaderInstalled => UiLocalization.Current["Darktide.Loader.StatusInstalled"],
            DarktideGameStatus.LoaderIncomplete => UiLocalization.Current["Darktide.Loader.StatusIncomplete"],
            DarktideGameStatus.GameWithoutLoader => UiLocalization.Current["Darktide.Loader.StatusMissing"],
            DarktideGameStatus.InvalidPath => UiLocalization.Current["Darktide.Loader.StatusInvalid"],
            _ => UiLocalization.Current["Darktide.Loader.StatusNotConfigured"]
        };
        AppDataPath = _storage.Root;
        ArchivePath = _storage.Archives;
        DatabasePath = _databasePathValue;
        ModsPath = state.RootPath == null ? "" : DarktidePaths.ModsDirectory(state.RootPath);
        LoaderBackupsPath = _storage.LoaderBackups;
        VersionText = typeof(DarktideSettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        // 页面刷新不能回读外观：那会冲掉用户尚未保存的滑块与背景图调整。
        return Task.CompletedTask;
    }

    /// <summary>外观在启动时就要应用一次，否则主题与透明度要等到打开设置页才生效。</summary>
    public void LoadAppearance()
    {
        _appearanceLoaded = false;
        SelectedThemeValue = _settings.GetSetting(SettingKeys.Theme, "Dark");
        CardOpacity = _settings.GetSetting(SettingKeys.CardOpacity, 100);
        CornerRadius = _settings.GetSetting(SettingKeys.CornerRadius, 8);
        BackgroundImagePath = _settings.GetSetting(SettingKeys.BackgroundImagePath, "");
        BackgroundImageOpacity = _settings.GetSetting(SettingKeys.BackgroundImageOpacity, 25);
        BackgroundBlur = _settings.GetSetting(SettingKeys.BackgroundBlur, 0);
        _appearanceLoaded = true;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAppearanceChanged()
    {
        if (_appearanceLoaded) AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedThemeValueChanged(string value) => NotifyAppearanceChanged();
    partial void OnCardOpacityChanged(double value) => NotifyAppearanceChanged();
    partial void OnCornerRadiusChanged(double value) => NotifyAppearanceChanged();
    partial void OnBackgroundImagePathChanged(string value) => NotifyAppearanceChanged();
    partial void OnBackgroundImageOpacityChanged(double value) => NotifyAppearanceChanged();
    partial void OnBackgroundBlurChanged(double value) => NotifyAppearanceChanged();

    [RelayCommand]
    private async Task PickBackgroundImageAsync()
    {
        var path = await _dialogs.PickImageAsync();
        if (path != null) BackgroundImagePath = path;
    }

    [RelayCommand]
    private void ClearBackgroundImage() => BackgroundImagePath = "";

    /// <summary>拖动滑块和换图只改预览，点保存才落盘，避免一次拖动写上百次设置。</summary>
    [RelayCommand]
    private async Task SaveAppearanceAsync()
    {
        await _settings.SetSettingAsync(SettingKeys.Theme, SelectedThemeValue);
        await _settings.SetSettingAsync(SettingKeys.CardOpacity, (int)Math.Round(CardOpacity));
        await _settings.SetSettingAsync(SettingKeys.CornerRadius, (int)Math.Round(CornerRadius));
        await _settings.SetSettingAsync(SettingKeys.BackgroundImagePath, BackgroundImagePath.Trim());
        await _settings.SetSettingAsync(SettingKeys.BackgroundImageOpacity, (int)Math.Round(BackgroundImageOpacity));
        await _settings.SetSettingAsync(SettingKeys.BackgroundBlur, (int)Math.Round(BackgroundBlur));
        Notifications.ShowSuccess(UiLocalization.Current["Darktide.Settings.AppearanceSaved"],
            UiLocalization.Current["Darktide.Settings.Title"]);
    }

    [RelayCommand]
    private async Task BrowseGameFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync();
        if (folder == null) return;
        GamePath = folder;
        await SaveGamePathAsync();
    }

    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var games = await Task.Run(_manager.FindInstalledGames, cancellationToken);
            if (games.Count == 0)
                throw new InvalidOperationException(UiLocalization.Current["Darktide.Settings.AutoDetectFailed"]);
            await _manager.SaveGamePathAsync(games[0], cancellationToken);
            await LoadAsync(cancellationToken);
            GamePathChanged?.Invoke(this, EventArgs.Empty);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Settings.AutoDetected", games[0]),
                UiLocalization.Current["Darktide.Settings.Title"]);
        }, failTitleKey: "Darktide.Settings.Title");
    }

    [RelayCommand]
    private async Task SaveGamePathAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            await _manager.SaveGamePathAsync(GamePath, cancellationToken);
            await LoadAsync(cancellationToken);
            GamePathChanged?.Invoke(this, EventArgs.Empty);
            Notifications.ShowSuccess(UiLocalization.Current["Darktide.Settings.Saved"],
                UiLocalization.Current["Darktide.Settings.Title"]);
        }, failTitleKey: "Darktide.Settings.Title");
    }

    [RelayCommand]
    private async Task ClearGamePathAsync()
    {
        GamePath = "";
        await RunAsync(async cancellationToken =>
        {
            await _manager.SaveGamePathAsync("", cancellationToken);
            await LoadAsync(cancellationToken);
            GamePathChanged?.Invoke(this, EventArgs.Empty);
        }, failTitleKey: "Darktide.Settings.Title");
    }

    [RelayCommand]
    private void OpenAppData() => _dialogs.OpenFolder(_storage.Root);

    [RelayCommand]
    private void OpenArchives() => _dialogs.OpenFolder(_storage.Archives);

    [RelayCommand]
    private void OpenMods()
    {
        if (Directory.Exists(ModsPath)) _dialogs.OpenFolder(ModsPath);
    }

    [RelayCommand]
    private void OpenLoaderBackups() => _dialogs.OpenFolder(_storage.LoaderBackups);
}
