using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace HD2Mimnt.UI.Controls;

public partial class NotificationPanelViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;

    [ObservableProperty]
    private ObservableCollection<Notification> _notifications = new();

    public ObservableCollection<Notification> Toasts { get; } = new();
    private readonly Dictionary<Guid, DispatcherTimer> _toastTimers = new();
    private bool _toastsEnabled;
    public bool IsHistoryOpen { get; set; }

    public void StartToasts() => _toastsEnabled = true;

    public void StopToasts()
    {
        _toastsEnabled = false;
        DismissAllToasts();
    }

    public void DismissAllToasts()
    {
        foreach (var timer in _toastTimers.Values) timer.Stop();
        _toastTimers.Clear();
        Toasts.Clear();
    }

    [RelayCommand]
    private void DismissToast(Notification notification)
    {
        if (_toastTimers.Remove(notification.Id, out var timer)) timer.Stop();
        Toasts.Remove(notification);
    }

    public void PauseToast(Notification notification, bool paused)
    {
        if (!_toastTimers.TryGetValue(notification.Id, out var timer)) return;
        if (paused) timer.Stop(); else timer.Start();
    }

    private void ShowToast(Notification notification)
    {
        if (!_toastsEnabled || IsHistoryOpen || notification.IsExpired) return;
        while (Toasts.Count >= 3) DismissToast(Toasts[^1]);
        Toasts.Insert(0, notification);
        // 横幅消失不删除通知历史。显式 DurationMs 的服务层过期规则仍然有效。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(
            notification.DurationMs > 0 ? notification.DurationMs :
            notification.Level == NotificationLevel.Error ? 10000 : 6000) };
        timer.Tick += (_, _) => DismissToast(notification);
        _toastTimers[notification.Id] = timer;
        timer.Start();
    }

    public event EventHandler<NotificationNavigationTarget>? NavigationRequested;

    /// <summary>请求关闭通知面板（由承载面板的窗口处理）。</summary>
    public event EventHandler? CloseRequested;

    public NotificationPanelViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;
        LoadNotifications();

        _notificationService.NotificationAdded += OnNotificationAdded;
        _notificationService.NotificationRemoved += OnNotificationRemoved;
    }

    private void LoadNotifications()
    {
        Notifications.Clear();
        foreach (var notification in _notificationService.GetRecentNotifications(50))
        {
            Notifications.Insert(0, notification);
        }
    }

    private void OnNotificationAdded(object? sender, Notification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (notification.IsExpired) return;
            Notifications.Insert(0, notification);
            ShowToast(notification);
        });
    }

    private void OnNotificationRemoved(object? sender, Guid notificationId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var notification = Notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null) Notifications.Remove(notification);
            var toast = Toasts.FirstOrDefault(n => n.Id == notificationId);
            if (toast != null) DismissToast(toast);
        });
    }

    [RelayCommand]
    private void RemoveNotification(Notification notification)
    {
        _notificationService.Remove(notification.Id);
    }

    [RelayCommand]
    private void ExecuteAction(Notification notification)
    {
        if (notification == null) return;

        _notificationService.MarkAsRead(notification.Id);
        notification.Action?.Invoke();
        if (notification.NavigationTarget is { } target)
        {
            NavigationRequested?.Invoke(this, target);
        }
        _notificationService.Remove(notification.Id);
    }

    [RelayCommand]
    private void ClearAll()
    {
        _notificationService.Clear();
        Notifications.Clear();
        DismissAllToasts();
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

public partial class NotificationPanel : UserControl
{
    public NotificationPanel()
    {
        InitializeComponent();
    }
}
