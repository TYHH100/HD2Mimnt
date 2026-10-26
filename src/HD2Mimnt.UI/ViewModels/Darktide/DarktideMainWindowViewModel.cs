using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideMainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DarktideManagerService _manager;
    private readonly NotificationService _notifications;
    private readonly TaskService _tasks;
    private readonly TaskCenterViewModel _taskCenter;
    private bool _disposed;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _gameStatusText = "";
    [ObservableProperty] private string _gamePathText = "";
    [ObservableProperty] private bool _gameDetected;
    [ObservableProperty] private bool _canLaunchGame;
    [ObservableProperty] private int _runningTaskCount;
    [ObservableProperty] private string _taskStatusText = "";
    [ObservableProperty] private bool _isTaskCenterOpen;
    [ObservableProperty] private bool _isNotificationPanelOpen;
    [ObservableProperty] private int _unreadNotificationCount;

    [ObservableProperty] private Bitmap? _backgroundImage;
    [ObservableProperty] private bool _hasBackgroundImage;
    [ObservableProperty] private double _backgroundImageOpacity = 0.25;
    [ObservableProperty] private double _backgroundBlurRadius;
    private bool _appearanceBusy;
    private bool _appearanceQueued;

    public DarktideMainWindowViewModel(DarktideManagerService manager, NotificationService notifications,
        TaskService tasks, DarktideLibraryViewModel library, DarktideLoadOrderViewModel loadOrder,
        DarktideProfilesViewModel profiles, DarktideLoaderViewModel loader, DarktideSettingsViewModel settings,
        TaskCenterViewModel taskCenter, NotificationPanelViewModel notificationPanel)
    {
        _manager = manager;
        _notifications = notifications;
        _tasks = tasks;
        _taskCenter = taskCenter;
        Library = library;
        LoadOrder = loadOrder;
        Profiles = profiles;
        Loader = loader;
        Settings = settings;
        NotificationPanel = notificationPanel;
        Title = UiLocalization.Current["Darktide.Title"];
        TaskStatusText = UiLocalization.Current["Darktide.Status.Ready"];
        _notifications.NotificationAdded += OnNotificationAdded;
        _notifications.NotificationRemoved += OnNotificationRemoved;
        _tasks.TaskStarted += OnTaskStarted;
        _tasks.TaskCompleted += OnTaskCompleted;
        Settings.GamePathChanged += (_, _) => UpdateGameStatus();
        Settings.AppearanceChanged += (_, _) => _ = ApplyAppearanceAsync();
        UiLocalization.Current.PropertyChanged += OnLocalizationChanged;
        NotificationPanel.NavigationRequested += OnNotificationNavigationRequested;
        NotificationPanel.CloseRequested += (_, _) => IsNotificationPanelOpen = false;
    }

    public DarktideLibraryViewModel Library { get; }
    public DarktideLoadOrderViewModel LoadOrder { get; }
    public DarktideProfilesViewModel Profiles { get; }
    public DarktideLoaderViewModel Loader { get; }
    public DarktideSettingsViewModel Settings { get; }
    public TaskCenterViewModel TaskCenter => _taskCenter;
    public NotificationPanelViewModel NotificationPanel { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        UpdateGameStatus();
        Settings.LoadAppearance();
        await ApplyAppearanceAsync();
        await UpdateRunningTasksAsync();
        await Library.RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 背景图在后台线程解码；滑块拖动会连续触发外观变化，解码期间只记一次补做，
    /// 换下的位图必须释放，否则反复改背景会累积原生内存。
    /// </summary>
    public async Task ApplyAppearanceAsync()
    {
        if (_disposed) return;
        if (_appearanceBusy)
        {
            _appearanceQueued = true;
            return;
        }
        _appearanceBusy = true;
        try
        {
            do
            {
                _appearanceQueued = false;
                var path = Settings.BackgroundImagePath.Trim();
                Bitmap? loaded = null;
                if (path.Length > 0 && File.Exists(path))
                {
                    try
                    {
                        loaded = await Task.Run(() => new Bitmap(path));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _notifications.ShowWarning(UiLocalization.Current["UI.FailedToLoadBackgroundImageUsingASolidBackground"],
                            UiLocalization.Current["UI.Appearance"]);
                        _ = ex;
                    }
                }
                var previous = BackgroundImage;
                BackgroundImage = loaded;
                HasBackgroundImage = loaded != null;
                BackgroundImageOpacity = Math.Clamp(Settings.BackgroundImageOpacity, 0, 100) / 100.0;
                BackgroundBlurRadius = Math.Clamp(Settings.BackgroundBlur, 0, 40);
                DarktideAppearance.Apply(Avalonia.Application.Current, Settings.SelectedThemeValue,
                    Settings.CardOpacity, Settings.CornerRadius, HasBackgroundImage);
                previous?.Dispose();
            } while (_appearanceQueued && !_disposed);
        }
        finally
        {
            _appearanceBusy = false;
        }
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        var result = await Task.Run(_manager.LaunchGame);
        if (!result.Started)
            _notifications.ShowError(result.Error ?? UiLocalization.Current["Darktide.Game.LaunchFailed"],
                UiLocalization.Current["Darktide.Game.Launch"]);
    }

    [RelayCommand]
    private void NavigateToLibrary() => SelectedTabIndex = 0;
    [RelayCommand]
    private void NavigateToLoadOrder() => SelectedTabIndex = 1;
    [RelayCommand]
    private void NavigateToProfiles() => SelectedTabIndex = 2;
    [RelayCommand]
    private void NavigateToLoader() => SelectedTabIndex = 3;
    [RelayCommand]
    private void NavigateToSettings() => SelectedTabIndex = 4;

    [RelayCommand]
    private async Task OpenTaskCenterAsync()
    {
        IsTaskCenterOpen = true;
        _taskCenter.StartAutoRefresh();
        await _taskCenter.LoadAsync();
    }

    [RelayCommand]
    private void CloseTaskCenter()
    {
        IsTaskCenterOpen = false;
        _taskCenter.StopAutoRefresh();
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        IsNotificationPanelOpen = true;
        NotificationPanel.IsHistoryOpen = true;
        NotificationPanel.DismissAllToasts();
        UnreadNotificationCount = _notifications.GetUnreadCount();
    }

    [RelayCommand]
    private void CloseNotifications()
    {
        IsNotificationPanelOpen = false;
        NotificationPanel.IsHistoryOpen = false;
    }

    [RelayCommand]
    private void CloseOverlays()
    {
        if (IsNotificationPanelOpen) CloseNotifications();
        if (IsTaskCenterOpen) CloseTaskCenter();
        Library.CloseOverlays();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        UpdateGameStatus();
        _ = RefreshPageAsync(value);
    }

    private async Task RefreshPageAsync(int index)
    {
        try
        {
            switch (index)
            {
                case 0: await Library.RefreshAsync(); break;
                case 1: await LoadOrder.RefreshAsync(); break;
                case 2: await Profiles.RefreshAsync(); break;
                case 3: await Loader.RefreshAsync(); break;
                case 4: await Settings.RefreshAsync(); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _notifications.ShowError(ex.Message, Title);
        }
    }

    private void UpdateGameStatus()
    {
        var state = _manager.GetGameState();
        GameDetected = state.Status == DarktideGameStatus.LoaderInstalled;
        CanLaunchGame = state.RootPath != null && state.HasGameExecutable;
        GameStatusText = state.Status switch
        {
            DarktideGameStatus.LoaderInstalled => UiLocalization.Current["Darktide.Status.LoaderInstalled"],
            DarktideGameStatus.LoaderIncomplete => UiLocalization.Current["Darktide.Status.LoaderIncomplete"],
            DarktideGameStatus.GameWithoutLoader => UiLocalization.Current["Darktide.Status.GameWithoutLoader"],
            DarktideGameStatus.InvalidPath => UiLocalization.Current["Darktide.Status.InvalidPath"],
            _ => UiLocalization.Current["Darktide.Status.NotConfigured"]
        };
        GamePathText = state.RootPath ?? UiLocalization.Current["Darktide.Game.NotSet"];
    }

    private async Task UpdateRunningTasksAsync()
    {
        var running = (await _tasks.GetRunningTasksAsync()).Count();
        RunningTaskCount = running;
        TaskStatusText = running == 0 ? UiLocalization.Current["Darktide.Status.Ready"]
            : UiLocalization.Current.Format("Darktide.Status.RunningTasks", running);
    }

    private void OnTaskStarted(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => _ = UpdateRunningTasksAsync());

    private void OnTaskCompleted(object? sender, TaskCompletedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            _ = UpdateRunningTasksAsync();
            if (SelectedTabIndex >= 0) _ = RefreshPageAsync(SelectedTabIndex);
        });

    private void OnNotificationAdded(object? sender, Notification notification)
        => Dispatcher.UIThread.Post(() => UnreadNotificationCount = _notifications.GetUnreadCount());

    private void OnNotificationRemoved(object? sender, Guid id)
        => Dispatcher.UIThread.Post(() => UnreadNotificationCount = _notifications.GetUnreadCount());

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Item[]" or "Revision")) return;
        Dispatcher.UIThread.Post(() =>
        {
            Title = UiLocalization.Current["Darktide.Title"];
            UpdateGameStatus();
        });
    }

    private void OnNotificationNavigationRequested(object? sender, NotificationNavigationTarget target)
    {
        IsNotificationPanelOpen = false;
        NotificationPanel.IsHistoryOpen = false;
        SelectedTabIndex = target switch
        {
            NotificationNavigationTarget.Profiles => 2,
            NotificationNavigationTarget.Settings => 4,
            NotificationNavigationTarget.Library => 0,
            _ => SelectedTabIndex
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _notifications.NotificationAdded -= OnNotificationAdded;
        _notifications.NotificationRemoved -= OnNotificationRemoved;
        _tasks.TaskStarted -= OnTaskStarted;
        _tasks.TaskCompleted -= OnTaskCompleted;
        UiLocalization.Current.PropertyChanged -= OnLocalizationChanged;
        NotificationPanel.NavigationRequested -= OnNotificationNavigationRequested;
        NotificationPanel.StopToasts();
        BackgroundImage?.Dispose();
        BackgroundImage = null;
        _taskCenter.StopAutoRefresh();
        Library.Cancel();
        LoadOrder.Cancel();
        Profiles.Cancel();
        Loader.Cancel();
        Settings.Cancel();
        await Task.CompletedTask;
    }
}
