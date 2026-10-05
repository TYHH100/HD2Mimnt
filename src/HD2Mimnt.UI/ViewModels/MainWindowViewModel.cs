using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Views;
using System.Collections.ObjectModel;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Services;
using Avalonia;
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
    private readonly SettingsViewModel _settingsViewModel;
    private readonly TaskCenterViewModel _taskCenterViewModel;
    private readonly ExternalToolsViewModel _externalToolsViewModel;
    private readonly PluginManagementViewModel _pluginManagementViewModel;
    private readonly BackupViewModel _backupViewModel;
    private readonly LogViewModel _logViewModel;
    private readonly ConfirmationService _confirmationService;

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

    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ProfileViewModel profileViewModel,
        GameDetectionService gameDetectionService,
        NotificationService notificationService,
        TaskService taskService,
        TestDataService testDataService,
        ProfileService profileService,
        SettingsViewModel settingsViewModel,
        TaskCenterViewModel taskCenterViewModel,
        NotificationPanelViewModel notificationPanelViewModel,
        ExternalToolsViewModel externalToolsViewModel,
        PluginManagementViewModel pluginManagementViewModel,
        BackupViewModel backupViewModel,
        LogViewModel logViewModel,
        ConfirmationService confirmationService)
    {
        _libraryViewModel = libraryViewModel;
        _profileViewModel = profileViewModel;
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _taskService = taskService;
        _testDataService = testDataService;
        _profileService = profileService;
        _settingsViewModel = settingsViewModel;
        _taskCenterViewModel = taskCenterViewModel;
        _externalToolsViewModel = externalToolsViewModel;
        _pluginManagementViewModel = pluginManagementViewModel;
        _backupViewModel = backupViewModel;
        _logViewModel = logViewModel;
        NotificationPanelViewModel = notificationPanelViewModel;
        _confirmationService = confirmationService;

        NavigateToLibrary();
        SubscribeToEvents();
        _profileViewModel.ActiveProfileChanged += OnActiveProfileChanged;
        _settingsViewModel.ThemeChanged += OnThemeChanged;
        _settingsViewModel.ListDensityChanged += OnListDensityChanged;
        _settingsViewModel.LibraryDefaultsChanged += OnLibraryDefaultsChanged;
        _backupViewModel.RestoreCompleted += OnBackupRestored;
        NotificationPanelViewModel.NavigationRequested += OnNotificationNavigationRequested;
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
                NavigateToMore();
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

    private async void OnBackupRestored(object? sender, EventArgs e)
    {
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
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        await _settingsViewModel.LoadAsync();
        ApplyTheme(_settingsViewModel.Theme);
        _libraryViewModel.ApplyListDensity(_settingsViewModel.ListDensity);
        _libraryViewModel.ApplyLibraryDefaults(_settingsViewModel.DefaultSortMode, _settingsViewModel.DefaultFilterType);
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
    private void NavigateToLibrary()
    {
        SelectedTabIndex = 1;
        CurrentView = _libraryViewModel;
        StatusBarText = "Library";
    }

    [RelayCommand]
    private void NavigateToProfiles()
    {
        SelectedTabIndex = 0;
        CurrentView = _profileViewModel;
        StatusBarText = "Profiles";
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
        SelectedTabIndex = 2;
        StatusBarText = "Settings";
    }

    [RelayCommand]
    private void NavigateToMore()
    {
        SelectedTabIndex = 3;
        StatusBarText = "More";
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

    [RelayCommand]
    private void CloseOverlays()
    {
        IsTaskCenterOpen = false;
        IsNotificationPanelOpen = false;
        _taskCenterViewModel.StopAutoRefresh();
    }

    public void AttachOwner(Avalonia.Controls.Window owner) => _confirmationService.SetOwner(owner);

    public async Task<bool> CanCloseAsync()
    {
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

    private void OnThemeChanged(object? sender, string theme) => ApplyTheme(theme);

    private void OnListDensityChanged(object? sender, string density) => _libraryViewModel.ApplyListDensity(density);

    private void OnLibraryDefaultsChanged(object? sender, EventArgs e) =>
        _libraryViewModel.ApplyLibraryDefaults(_settingsViewModel.DefaultSortMode, _settingsViewModel.DefaultFilterType);

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
        var devToolsViewModel = new DevToolsViewModel(_testDataService, _profileService);
        var devToolsWindow = new DevToolsWindow
        {
            DataContext = devToolsViewModel
        };
        devToolsWindow.Show();
    }
}
