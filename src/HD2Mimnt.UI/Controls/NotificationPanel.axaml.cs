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

    public event EventHandler<NotificationNavigationTarget>? NavigationRequested;

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
        Dispatcher.UIThread.Post(() => Notifications.Insert(0, notification));
    }

    private void OnNotificationRemoved(object? sender, Guid notificationId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var notification = Notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null) Notifications.Remove(notification);
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
    }

    [RelayCommand]
    private void Close()
    {
        // This will be handled by the parent window/popup
    }
}

public partial class NotificationPanel : UserControl
{
    public NotificationPanel()
    {
        InitializeComponent();
    }
}
