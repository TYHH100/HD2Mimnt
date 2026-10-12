using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-settings-events-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetSettingAsync_Notification_ShouldAllowReentrantRead()
    {
        var service = new SettingsService(_root);
        string? observed = null;
        service.SettingChanged += (_, args) => observed = service.GetSetting(args.Key, string.Empty);

        await Task.Run(() => service.SetSettingAsync(SettingKeys.Language, "en-US"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("en-US", observed);
    }

    [Fact]
    public async Task ReloadAsync_UnchangedJsonValues_ShouldNotNotify()
    {
        var service = new SettingsService(_root);
        await service.SetSettingAsync(SettingKeys.Language, "zh-TW");
        await service.SetSettingAsync(SettingKeys.ShowModThumbnails, true);
        var count = 0;
        service.SettingChanged += (_, _) => count++;

        await service.ReloadAsync();
        await service.ReloadAsync();

        Assert.Equal(0, count);
        Assert.Equal("zh-TW", service.GetSetting(SettingKeys.Language, "auto"));
    }

    [Fact]
    public async Task ResetCategoryAsync_ShouldPublishOnlyRemovedKeysAfterSuccessfulWrite()
    {
        var service = new SettingsService(_root);
        await service.SetSettingAsync(SettingKeys.Language, "zh-TW");
        await service.SetSettingAsync(SettingKeys.GamePath, "game");
        var changes = new List<SettingChangedEventArgs>();
        service.SettingChanged += (_, args) => changes.Add(args);

        await service.ResetCategoryAsync("UI");

        var removed = Assert.Single(changes);
        Assert.Equal(SettingKeys.Language, removed.Key);
        Assert.Equal("zh-TW", removed.OldValue);
        Assert.Null(removed.NewValue);
        Assert.Equal("game", service.GetSetting(SettingKeys.GamePath, string.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
