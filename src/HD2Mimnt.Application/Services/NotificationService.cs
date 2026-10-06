namespace HD2Mimnt.Application.Services;

/// <summary>
/// 通知服务
/// 负责管理应用内的通知消息
/// </summary>
public class NotificationService
{
    private readonly List<Notification> _notifications = new();
    private readonly HashSet<string> _recentMessages = new();
    private readonly object _lock = new();
    private const int MaxNotifications = 100;
    private const int DuplicateWindowMs = 5000;

    public event EventHandler<Notification>? NotificationAdded;
    public event EventHandler<Guid>? NotificationRemoved;

    /// <summary>
    /// 显示成功通知（不自动过期，由用户在通知面板手动删除）
    /// </summary>
    public void ShowSuccess(string message, string? title = null, int durationMs = 0)
    {
        AddNotification(NotificationLevel.Success, message, title, durationMs);
    }

    /// <summary>
    /// 显示信息通知
    /// </summary>
    public void ShowInfo(string message, string? title = null, int durationMs = 0)
    {
        AddNotification(NotificationLevel.Info, message, title, durationMs);
    }

    /// <summary>
    /// 显示警告通知
    /// </summary>
    public void ShowWarning(string message, string? title = null, int durationMs = 0)
    {
        AddNotification(NotificationLevel.Warning, message, title, durationMs);
    }

    /// <summary>
    /// 显示错误通知
    /// </summary>
    public void ShowError(string message, string? title = null, int durationMs = 0)
    {
        AddNotification(NotificationLevel.Error, message, title, durationMs);
    }

    /// <summary>
    /// 显示带操作的通知
    /// </summary>
    public void ShowWithAction(
        NotificationLevel level,
        string message,
        string actionText,
        Action action,
        string? title = null,
        int durationMs = 0)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Level = level,
            Message = message,
            Title = title,
            Timestamp = DateTime.UtcNow,
            IsRead = false,
            ActionText = actionText,
            Action = action,
            DurationMs = durationMs
        };

        AddNotificationInternal(notification);
    }

    public void ShowWithNavigation(
        NotificationLevel level,
        string message,
        string actionText,
        NotificationNavigationTarget target,
        string? title = null,
        int durationMs = 0)
    {
        AddNotificationInternal(new Notification
        {
            Id = Guid.NewGuid(),
            Level = level,
            Message = message,
            Title = title,
            Timestamp = DateTime.UtcNow,
            IsRead = false,
            ActionText = actionText,
            NavigationTarget = target,
            DurationMs = durationMs
        });
    }

    /// <summary>
    /// 获取最近的通知
    /// </summary>
    public IEnumerable<Notification> GetRecentNotifications(int limit = 50)
    {
        lock (_lock)
        {
            return _notifications.AsEnumerable().Reverse().Take(limit).ToList();
        }
    }

    /// <summary>
    /// 获取未读通知数量
    /// </summary>
    public int GetUnreadCount()
    {
        lock (_lock)
        {
            return _notifications.Count(n => !n.IsRead);
        }
    }

    /// <summary>
    /// 标记通知为已读
    /// </summary>
    public void MarkAsRead(Guid notificationId)
    {
        lock (_lock)
        {
            var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null)
            {
                notification.IsRead = true;
            }
        }
    }

    /// <summary>
    /// 标记所有通知为已读
    /// </summary>
    public void MarkAllAsRead()
    {
        lock (_lock)
        {
            foreach (var notification in _notifications)
            {
                notification.IsRead = true;
            }
        }
    }

    /// <summary>
    /// 删除通知
    /// </summary>
    public void Remove(Guid notificationId)
    {
        var removed = false;
        lock (_lock)
        {
            removed = _notifications.RemoveAll(n => n.Id == notificationId) > 0;
        }

        if (removed)
        {
            NotificationRemoved?.Invoke(this, notificationId);
        }
    }

    /// <summary>
    /// 清空所有通知
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _notifications.Clear();
            _recentMessages.Clear();
        }
    }

    /// <summary>
    /// 清理过期的重复消息记录
    /// </summary>
    private void CleanupRecentMessages()
    {
        lock (_lock)
        {
            // 简单实现：定期清空
            // 在生产环境中应该使用带时间戳的字典
            if (_recentMessages.Count > 100)
            {
                _recentMessages.Clear();
            }
        }
    }

    private void AddNotification(
        NotificationLevel level,
        string message,
        string? title,
        int durationMs)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Level = level,
            Message = message,
            Title = title,
            Timestamp = DateTime.UtcNow,
            IsRead = false,
            DurationMs = durationMs
        };

        AddNotificationInternal(notification);
    }

    private void AddNotificationInternal(Notification notification)
    {
        // 检查是否为重复消息
        var messageKey = $"{notification.Level}:{notification.Message}";

        lock (_lock)
        {
            if (_recentMessages.Contains(messageKey))
            {
                // 重复消息，忽略
                return;
            }

            _recentMessages.Add(messageKey);

            // 启动定时器清理重复消息记录
            _ = Task.Delay(DuplicateWindowMs).ContinueWith(_ =>
            {
                lock (_lock)
                {
                    _recentMessages.Remove(messageKey);
                }
            });
        }

        lock (_lock)
        {
            _notifications.Add(notification);

            // 限制通知数量，保留最新通知。
            if (_notifications.Count > MaxNotifications)
            {
                _notifications.RemoveRange(0, _notifications.Count - MaxNotifications);
            }
        }

        // 触发事件
        NotificationAdded?.Invoke(this, notification);

        // 如果设置了自动消失时间，启动定时器
        if (notification.DurationMs > 0)
        {
            notification.ExpirationTimer = new Timer(static state =>
            {
                var context = ((NotificationService Service, Notification Notification))state!;
                context.Notification.IsExpired = true;
                context.Service.Remove(context.Notification.Id);
                context.Notification.ExpirationTimer?.Dispose();
            }, (this, notification), notification.DurationMs, Timeout.Infinite);
        }

        // 定期清理重复消息记录
        CleanupRecentMessages();
    }
}

/// <summary>
/// 通知
/// </summary>
public class Notification
{
    public Guid Id { get; set; }
    public NotificationLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsRead { get; set; }
    public bool IsExpired { get; set; }
    public string? ActionText { get; set; }
    public Action? Action { get; set; }
    public NotificationNavigationTarget? NavigationTarget { get; set; }
    public bool HasAction => Action != null || NavigationTarget.HasValue || !string.IsNullOrWhiteSpace(ActionText);
    public int DurationMs { get; set; }
    internal Timer? ExpirationTimer { get; set; }
}

public enum NotificationNavigationTarget
{
    Library,
    Profiles,
    Settings,
    More,
    Tasks
}

/// <summary>
/// 通知级别
/// </summary>
public enum NotificationLevel
{
    Success,
    Info,
    Warning,
    Error
}
