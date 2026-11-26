using Microsoft.Extensions.Logging;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class NotificationServiceTests
{
    [Fact]
    public void Remove_Should_Remove_Notification_From_Service()
    {
        var service = new NotificationService();
        service.ShowInfo("message", "title", durationMs: 0);

        var notification = Assert.Single(service.GetRecentNotifications());
        service.Remove(notification.Id);

        Assert.Empty(service.GetRecentNotifications());
        Assert.Equal(0, service.GetUnreadCount());
    }

    [Fact]
    public async Task TimedNotification_Should_Be_Removed_After_Duration()
    {
        var service = new NotificationService();
        service.ShowInfo("temporary", durationMs: 40);

        await Task.Delay(160);

        Assert.Empty(service.GetRecentNotifications());
    }

    [Fact]
    public void DuplicateMessages_Should_Be_Coalesced_Within_Window()
    {
        var service = new NotificationService();
        service.ShowWarning("same", "warning", durationMs: 0);
        service.ShowWarning("same", "warning", durationMs: 0);

        Assert.Single(service.GetRecentNotifications());
    }

    [Fact]
    public void ShowWithNavigation_ShouldExposeTargetAndAction()
    {
        var service = new NotificationService();
        service.ShowWithNavigation(NotificationLevel.Warning, "invalid", "打开设置", NotificationNavigationTarget.Settings);

        var notification = Assert.Single(service.GetRecentNotifications());
        Assert.True(notification.HasAction);
        Assert.Equal(NotificationNavigationTarget.Settings, notification.NavigationTarget);
    }

    /// <summary>通知只有内存面板，警告/错误必须同步写日志，否则重启后无法提交问题给开发者。</summary>
    [Fact]
    public void WarningAndErrorNotifications_Should_Be_Logged()
    {
        var logger = new RecordingLogger();
        var service = new NotificationService(logger);

        service.ShowInfo("info", "i", durationMs: 0);
        service.ShowSuccess("success", "s", durationMs: 0);
        service.ShowWarning("careful", "warn", durationMs: 0);
        service.ShowError("boom", "err", durationMs: 0);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("warn：", warning.Message);
        Assert.Contains("careful", warning.Message);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("err：", error.Message);
        Assert.Contains("boom", error.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("info"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("success"));
    }

    private sealed class RecordingLogger : ILogger<NotificationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
