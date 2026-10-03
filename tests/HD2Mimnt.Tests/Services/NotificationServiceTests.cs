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
}
