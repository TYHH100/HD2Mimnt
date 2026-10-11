using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Views;
using System.Collections.ObjectModel;
using System.IO;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Services;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace HD2Mimnt.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly LibraryViewModel _libraryViewModel;
    private readonly ProfileViewModel _profileViewModel;
    private readonly GameDetectionService _gameDetectionService;
    private readonly NotificationService _notificationService;
    private readonly TaskService _taskService;
    private readonly TestDataService _testDataService;
    private readonly ProfileService _profileService;
    private readonly ModService _modService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly TaskCenterViewModel _taskCenterViewModel;
    private readonly ExternalToolsViewModel _externalToolsViewModel;
    private readonly PluginManagementViewModel _pluginManagementViewModel;
    private readonly BackupViewModel _backupViewModel;
    private readonly LogViewModel _logViewModel;
    private readonly ConfirmationService _confirmationService;
    private readonly ConflictsViewModel _conflictsViewModel;
    private readonly ImagePreviewService _imagePreview;
    private readonly NexusModsTestService _nexusModsTestService;
    private int _tutorialOriginPage;
    private string? _tutorialOriginSettingsCategory;
    private string? _tutorialOriginToolsCategory;

    [ObservableProperty]
    private string _title = "HD2Mimnt - Helldivers 2 Mod Manager";

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    // 游戏状态
    [ObservableProperty]
    private string _gameStatus = "Detecting...";

    [ObservableProperty]
    private bool _gameDetected = false;

    [ObservableProperty]
    private string _gamePath = string.Empty;

    // 当前配置文件
    [ObservableProperty]
    private string _currentProfileName = "No Profile";

    // 部署状态
    [ObservableProperty]
    private string _deploymentStatus = "Not Deployed";

    [ObservableProperty]
    private bool _isDeployed = false;

    // 任务状态
    [ObservableProperty]
    private int _runningTaskCount = 0;

    [ObservableProperty]
    private string _taskStatusText = "Idle";

    // 通知状态
    [ObservableProperty]
    private int _unreadNotificationCount = 0;

    [ObservableProperty]
    private bool _isTaskCenterOpen;

    [ObservableProperty]
    private bool _isNotificationPanelOpen;

    // 外观：自定义背景图片与卡片透明度（设置页“外观”驱动）
    [ObservableProperty]
    private Bitmap? _backgroundImage;

    [ObservableProperty]
    private bool _hasBackgroundImage;

    [ObservableProperty]
    private double _backgroundImageOpacity = 0.25;

    [ObservableProperty]
    private double _backgroundBlurRadius;

    // Expose ViewModels for binding
    public LibraryViewModel LibraryViewModel => _libraryViewModel;
    public ProfileViewModel ProfileViewModel => _profileViewModel;
    public SettingsViewModel SettingsViewModel => _settingsViewModel;
    public TaskCenterViewModel TaskCenterViewModel => _taskCenterViewModel;
    public ExternalToolsViewModel ExternalToolsViewModel => _externalToolsViewModel;
    public PluginManagementViewModel PluginManagementViewModel => _pluginManagementViewModel;
    public BackupViewModel BackupViewModel => _backupViewModel;
    public LogViewModel LogViewModel => _logViewModel;
    public NotificationPanelViewModel NotificationPanelViewModel { get; }
    public MoreViewModel MoreViewModel { get; } = new();
    public ConflictsViewModel ConflictsViewModel => _conflictsViewModel;
    public ImagePreviewService ImagePreview => _imagePreview;
    public TutorialViewModel Tutorial { get; } = new();

    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ProfileViewModel profileViewModel,
        ConflictsViewModel conflictsViewModel,
        GameDetectionService gameDetectionService,
        NotificationService notificationService,
        TaskService taskService,
        TestDataService testDataService,
        ProfileService profileService,
        ModService modService,
        SettingsViewModel settingsViewModel,
        TaskCenterViewModel taskCenterViewModel,
        NotificationPanelViewModel notificationPanelViewModel,
        ExternalToolsViewModel externalToolsViewModel,
        PluginManagementViewModel pluginManagementViewModel,
        BackupViewModel backupViewModel,
        LogViewModel logViewModel,
        ConfirmationService confirmationService,
        ImagePreviewService imagePreviewService,
        NexusModsTestService nexusModsTestService)
    {
        _libraryViewModel = libraryViewModel;
        _profileViewModel = profileViewModel;
        _conflictsViewModel = conflictsViewModel;
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _taskService = taskService;
        _testDataService = testDataService;
        _profileService = profileService;
        _modService = modService;
        _settingsViewModel = settingsViewModel;
        _taskCenterViewModel = taskCenterViewModel;
        _externalToolsViewModel = externalToolsViewModel;
        _pluginManagementViewModel = pluginManagementViewModel;
        _backupViewModel = backupViewModel;
        _logViewModel = logViewModel;
        NotificationPanelViewModel = notificationPanelViewModel;
        _confirmationService = confirmationService;
        _imagePreview = imagePreviewService;
        _nexusModsTestService = nexusModsTestService;
        Tutorial.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TutorialViewModel.IsOpen))
            {
                if (Tutorial.IsOpen)
                {
                    _tutorialOriginPage = SelectedTabIndex;
                    _tutorialOriginSettingsCategory = SettingsViewModel.SelectedCategory;
                    _tutorialOriginToolsCategory = MoreViewModel.SelectedCategory;
                }
                else
                {
                    SettingsViewModel.SelectedCategory = _tutorialOriginSettingsCategory ?? "游戏";
                    MoreViewModel.SelectedCategory = _tutorialOriginToolsCategory ?? "插件";
                    NavigateToPage(_tutorialOriginPage);
                    return;
                }
            }
            if (Tutorial.IsOpen && (args.PropertyName == nameof(TutorialViewModel.CurrentStep)
                || args.PropertyName == nameof(TutorialViewModel.IsOpen)))
            {
                CloseTaskCenter();
                CloseNotifications();
                NavigateForTutorialStep();
            }
        };

        NavigateToProfiles();
        SubscribeToEvents();
        _profileViewModel.ActiveProfileChanged += OnActiveProfileChanged;
        _profileViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProfileViewModel.SelectedProfile)) SyncConflictProfile();
        };
        _settingsViewModel.ThemeChanged += OnThemeChanged;
        _settingsViewModel.ListDensityChanged += OnListDensityChanged;
        _settingsViewModel.AppearanceChanged += OnAppearanceChanged;
        _backupViewModel.RestoreCompleted += OnBackupRestored;
        NotificationPanelViewModel.NavigationRequested += OnNotificationNavigationRequested;
        NotificationPanelViewModel.CloseRequested += (_, _) => CloseNotifications();
        _modService.ModsChanged += OnModsChanged;
        _profileService.ProfilesChanged += OnProfilesChanged;
        _profileViewModel.DeploymentDirectionChanged += (_, _) => _conflictsViewModel.InvalidateResults();
    }

    private void NavigateForTutorialStep()
    {
        if (Tutorial.CurrentStep.PageIndex == 3)
            MoreViewModel.SelectedCategory = Tutorial.CurrentStep.Category ?? "插件";
        if (Tutorial.CurrentStep.PageIndex == 4)
            SettingsViewModel.SelectedCategory = Tutorial.CurrentStep.Category ?? "游戏";
        NavigateToPage(Tutorial.CurrentStep.PageIndex);
    }

    private void NavigateToPage(int pageIndex)
    {
        switch (pageIndex)
        {
            case 0: NavigateToProfiles(); break;
            case 1: NavigateToLibrary(); break;
            case 2: NavigateToConflicts(); break;
            case 3: NavigateToTools(); break;
            case 4: NavigateToSettings(); break;
        }
    }

    private void SubscribeToEvents()
    {
        _gameDetectionService.GameStatusChanged += OnGameStatusChanged;
        _notificationService.NotificationAdded += OnNotificationAdded;
        _taskService.TaskCompleted += OnTaskCompleted;
    }

    private void OnGameStatusChanged(object? sender, GameStatusChangedEventArgs e)
    {
        ApplyOnUiThread(() =>
        {
            GameDetected = e.NewStatus == Application.Services.GameStatus.Found;
            GamePath = e.GameInfo.Path;
            GameStatus = e.NewStatus switch
            {
                Application.Services.GameStatus.Found => "Game Found",
                Application.Services.GameStatus.NotFound => "Game Not Found",
                Application.Services.GameStatus.Invalid => "Invalid Path",
                Application.Services.GameStatus.Checking => "Checking...",
                _ => "Unknown"
            };
        });
    }

    private void OnNotificationAdded(object? sender, Notification e)
    {
        ApplyOnUiThread(() => UnreadNotificationCount = _notificationService.GetUnreadCount());
    }

    private static void ApplyOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess() || global::Avalonia.Application.Current is null)
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private void OnTaskCompleted(object? sender, TaskCompletedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = HandleTaskCompletedAsync(e.Task);
            return;
        }

        Dispatcher.UIThread.Post(() => _ = HandleTaskCompletedAsync(e.Task));
    }

    private async Task HandleTaskCompletedAsync(Core.Entities.ModTask task)
    {
        switch (task.Status)
        {
            case Core.Entities.TaskStatus.Completed:
                _notificationService.ShowSuccess($"任务“{task.Name}”已完成", "后台任务");
                break;
            case Core.Entities.TaskStatus.PartiallyFailed:
                _notificationService.ShowWarning($"任务“{task.Name}”部分失败", "后台任务");
                break;
            case Core.Entities.TaskStatus.Cancelled:
                _notificationService.ShowInfo($"任务“{task.Name}”已取消", "后台任务");
                break;
            case Core.Entities.TaskStatus.Failed:
            case Core.Entities.TaskStatus.ForceTerminated:
                _notificationService.ShowError($"任务“{task.Name}”执行失败：{task.Message}", "后台任务");
                break;
        }

        await UpdateRunningTasksAsync();
    }

    private void OnNotificationNavigationRequested(object? sender, NotificationNavigationTarget target)
    {
        IsNotificationPanelOpen = false;
        switch (target)
        {
            case NotificationNavigationTarget.Library:
                NavigateToLibrary();
                break;
            case NotificationNavigationTarget.Profiles:
                NavigateToProfiles();
                break;
            case NotificationNavigationTarget.Settings:
                NavigateToSettings();
                break;
            case NotificationNavigationTarget.More:
                NavigateToTools();
                break;
            case NotificationNavigationTarget.Tasks:
                NavigateToTasks();
                break;
        }
    }

    private async void OnActiveProfileChanged(object? sender, EventArgs e)
    {
        await _libraryViewModel.RefreshProfileStateAsync();
        UpdateCurrentProfile();
    }

    private void OnModsChanged(object? sender, EventArgs e)
    {
        // 模组增删后联动刷新配置文件页（库页自行重载）
        ApplyOnUiThread(async () =>
        {
            _conflictsViewModel.InvalidateResults();
            await _profileViewModel.RefreshAfterProfilesChangedAsync();
        });
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        // 配置文件数据变更（含模组成员/启用状态）联动刷新配置文件页与库页统计、归属状态
        ApplyOnUiThread(async () =>
        {
            _conflictsViewModel.InvalidateResults();
            await _profileViewModel.RefreshAfterProfilesChangedAsync();
            await _libraryViewModel.RefreshProfileStateAsync();
            SyncConflictProfile();
            UpdateCurrentProfile();
        });
    }

    private async void OnBackupRestored(object? sender, EventArgs e)
    {
        await _profileService.InitializeAsync();
        await _settingsViewModel.LoadAsync();
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        await _taskCenterViewModel.LoadAsync();
        await _externalToolsViewModel.InitializeAsync();
        await _pluginManagementViewModel.InitializeAsync();
        UpdateCurrentProfile();
    }

    public async Task InitializeAsync()
    {
        await _profileService.InitializeAsync();
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        SyncConflictProfile();
        await _settingsViewModel.LoadAsync();
        ApplyTheme(_settingsViewModel.Theme);
        ApplyTransparencyBrushes();
        _libraryViewModel.ApplyListDensity(_settingsViewModel.ListDensity);
        await ApplyAppearanceAsync();
        await _taskCenterViewModel.LoadAsync();
        await _externalToolsViewModel.InitializeAsync();
        await _pluginManagementViewModel.InitializeAsync();
        await _backupViewModel.InitializeAsync();
        await _logViewModel.InitializeAsync();
        await UpdateRunningTasksAsync();

        // 根据设置决定是否扫描常见安装位置。
        if (_settingsViewModel.AutoDetectGame)
        {
            await _gameDetectionService.AutoDetectAsync();
        }
        else
        {
            await _gameDetectionService.ApplyConfiguredPathAsync(_settingsViewModel.GamePath);
        }

        // 更新当前配置
        UpdateCurrentProfile();

        // 显示欢迎通知
        _notificationService.ShowInfo("HD2Mimnt started successfully", "Welcome");

        StatusBarText = "Application initialized";
    }

    private void UpdateCurrentProfile()
    {
        var activeProfile = _profileViewModel.Profiles.FirstOrDefault(p => p.IsActive);
        CurrentProfileName = activeProfile?.Name ?? "No Profile";
    }

    [RelayCommand]
    private void NavigateToProfiles()
    {
        SelectedTabIndex = 0;
        CurrentView = _profileViewModel;
        StatusBarText = "配置文件";
    }

    [RelayCommand]
    private void NavigateToLibrary()
    {
        SelectedTabIndex = 1;
        CurrentView = _libraryViewModel;
        StatusBarText = "模组库";
    }

    [RelayCommand]
    private void NavigateToConflicts()
    {
        SyncConflictProfile();
        SelectedTabIndex = 2;
        CurrentView = _conflictsViewModel;
        StatusBarText = "冲突";
    }

    private void SyncConflictProfile() => _conflictsViewModel.SetProfile(
        _profileViewModel.SelectedProfile?.Id, _profileViewModel.SelectedProfile?.Name);

    /// <summary>配置文件页“检查冲突”入口：跳转冲突页并立即启动扫描。</summary>
    [RelayCommand]
    private void OpenConflictsAndScan()
    {
        NavigateToConflicts();
        _ = _conflictsViewModel.ScanAsync();
    }

    [RelayCommand]
    private void NavigateToTasks()
    {
        IsTaskCenterOpen = true;
        _taskCenterViewModel.StartAutoRefresh();
        _ = UpdateRunningTasksAsync();
        StatusBarText = "任务中心";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 4;
        StatusBarText = "设置";
    }

    [RelayCommand]
    private void NavigateToTools()
    {
        SelectedTabIndex = 3;
        StatusBarText = "工具";
    }

    [RelayCommand]
    private void CloseTaskCenter()
    {
        IsTaskCenterOpen = false;
        _taskCenterViewModel.StopAutoRefresh();
    }

    [RelayCommand]
    private void OpenTaskCenter()
    {
        NavigateToTasks();
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        IsNotificationPanelOpen = !IsNotificationPanelOpen;
        if (IsNotificationPanelOpen)
        {
            _notificationService.MarkAllAsRead();
            UnreadNotificationCount = 0;
        }
    }

    [RelayCommand]
    private void CloseNotifications() => IsNotificationPanelOpen = false;

    partial void OnIsNotificationPanelOpenChanged(bool value)
    {
        NotificationPanelViewModel.IsHistoryOpen = value;
        if (value) NotificationPanelViewModel.DismissAllToasts();
    }

    [RelayCommand]
    private void CloseOverlays()
    {
        if (Tutorial.IsOpen)
        {
            Tutorial.CloseCommand.Execute(null);
            return;
        }
        if (_libraryViewModel.Import.IsOpen)
        {
            _libraryViewModel.Import.CloseCommand.Execute(null);
            return;
        }
        if (_imagePreview.IsOpen)
        {
            _imagePreview.Close();
            return;
        }

        if (SelectedTabIndex == 0 && !IsTaskCenterOpen && !IsNotificationPanelOpen)
        {
            if (_profileViewModel.IsMoveToPositionDialogOpen)
                _profileViewModel.CancelMoveToPositionCommand.Execute(null);
            else if (_profileViewModel.IsModOptionsDialogOpen)
                _profileViewModel.CloseModOptionsCommand.Execute(null);
            else if (_profileViewModel.IsAddModOpen)
                _profileViewModel.CloseAddModCommand.Execute(null);
            else if (!_libraryViewModel.IsDeploying)
                _profileViewModel.ClearModSelectionCommand.Execute(null);
        }
        if (SelectedTabIndex == 1 && !IsTaskCenterOpen && !IsNotificationPanelOpen)
        {
            if (_libraryViewModel.IsAddToProfileDialogOpen)
                _libraryViewModel.CancelAddToProfileCommand.Execute(null);
            else if (_libraryViewModel.IsDetailPanelOpen)
                _libraryViewModel.CloseDetailPanelCommand.Execute(null);
            else
                _libraryViewModel.ClearSelectionCommand.Execute(null);
        }

        IsTaskCenterOpen = false;
        IsNotificationPanelOpen = false;
        _taskCenterViewModel.StopAutoRefresh();
    }

    public void AttachOwner(Avalonia.Controls.Window owner) => _confirmationService.SetOwner(owner);

    public async Task<bool> CanCloseAsync()
    {
        if (!_libraryViewModel.ImportCompletion.IsCompleted)
        {
            if (!await _confirmationService.ConfirmAsync("退出应用", "模组仍在添加中。是否取消添加并在清理完成后退出？"))
                return false;
            _libraryViewModel.Import.CancelCommand.Execute(null);
            await _libraryViewModel.ImportCompletion;
        }
        var runningTasks = await _taskService.GetRunningTasksAsync();
        if (!runningTasks.Any())
        {
            _taskCenterViewModel.StopAutoRefresh();
            return true;
        }

        var canClose = await _confirmationService.ConfirmAsync(
            "退出应用",
            $"当前仍有 {runningTasks.Count()} 个后台任务运行中。退出可能中断正在进行的操作，确定退出吗？");
        if (canClose)
        {
            _taskCenterViewModel.StopAutoRefresh();
        }

        return canClose;
    }

    private void OnThemeChanged(object? sender, string theme)
    {
        ApplyTheme(theme);
        ApplyTransparencyBrushes();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => _ = ApplyAppearanceAsync();

    /// <summary>应用外观设置：背景图片（后台线程解码）、透明度、模糊度与卡片透明度。</summary>
    public async Task ApplyAppearanceAsync()
    {
        BackgroundImageOpacity = Math.Clamp(_settingsViewModel.BackgroundImageOpacity, 0, 100) / 100.0;
        BackgroundBlurRadius = Math.Clamp(_settingsViewModel.BackgroundBlur, 0, 40);

        var path = _settingsViewModel.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundImage = null;
            HasBackgroundImage = false;
        }
        else
        {
            try
            {
                var bitmap = await Task.Run(() => new Bitmap(path));
                BackgroundImage = bitmap;
                HasBackgroundImage = true;
            }
            catch
            {
                BackgroundImage = null;
                HasBackgroundImage = false;
                _notificationService.ShowWarning("背景图片加载失败，已回退为纯色背景", "外观");
            }
        }

        ApplyTransparencyBrushes();
    }

    /// <summary>只更新表面透明度，颜色由 FluentAvalonia 动态资源随主题更新。卡片半透明透出背景图；
    /// 有背景图时顶栏/底栏跟随卡片透明度、内容区全透明露出背景图；无背景图时全部恢复不透明（默认观感）。</summary>
    private void ApplyTransparencyBrushes()
    {
        var app = global::Avalonia.Application.Current;
        if (app == null) return;

        var percent = Math.Clamp(_settingsViewModel.CardOpacity, 5, 100);
        app.Resources["CardSurfaceOpacity"] = percent / 100.0;
        app.Resources["ChromeSurfaceOpacity"] = HasBackgroundImage ? percent / 100.0 : 1.0;
        app.Resources["ContentSurfaceOpacity"] = HasBackgroundImage ? 0.0 : 1.0;
    }

    private void OnListDensityChanged(object? sender, string density) => _libraryViewModel.ApplyListDensity(density);


    private static void ApplyTheme(string theme)
    {
        if (global::Avalonia.Application.Current == null) return;
        global::Avalonia.Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    [RelayCommand]
    private async Task RefreshGameStatusAsync()
    {
        GameStatus = "Checking...";
        await _gameDetectionService.AutoDetectAsync();
    }

    [RelayCommand]
    private async Task UpdateRunningTasksAsync()
    {
        var runningTasks = await _taskService.GetRunningTasksAsync();
        RunningTaskCount = runningTasks.Count();
        TaskStatusText = RunningTaskCount > 0 ? $"{RunningTaskCount} Running" : "Idle";
    }

    [RelayCommand]
    private void OpenDevTools()
    {
        var devToolsViewModel = new DevToolsViewModel(_testDataService, _profileService, _nexusModsTestService);
        var devToolsWindow = new DevToolsWindow
        {
            DataContext = devToolsViewModel
        };
        devToolsWindow.Show();
    }
}
