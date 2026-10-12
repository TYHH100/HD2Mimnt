using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Tests.Services;

public sealed class SettingsDatabaseStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-SettingsDbMove-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitializeAsync_MovedStorage_ShouldUpdateDatabaseBackgroundPath(bool moveAppData)
    {
        var store = new StorageLocationStore(Path.Combine(_root, "data"));
        var storage = new StorageSettingsService(store);
        await storage.InitializeAsync();
        var before = storage.Current;
        var background = Path.Combine(before.ModsPath, "background.png");
        await File.WriteAllTextAsync(background, "image");
        var settings = new SettingsService(before.AppDataPath);
        await settings.SetSettingAsync(SettingKeys.BackgroundImagePath, background);
        await settings.SetSettingAsync(SettingKeys.Language, "zh-TW");
        var next = moveAppData
            ? new StorageLocations(Path.Combine(_root, "new-data"), Path.Combine(_root, "new-mods"), Path.Combine(_root, "new-cache"))
            : before with { ModsPath = Path.Combine(_root, "new-mods") };
        await storage.SaveAsync(next);

        await storage.InitializeAsync();

        Assert.Null(storage.StartupMessage);
        var reopened = new SettingsService(storage.Current.AppDataPath);
        var newBackground = Path.Combine(next.ModsPath, "background.png");
        Assert.Equal(newBackground, reopened.GetSetting(SettingKeys.BackgroundImagePath, ""));
        Assert.Equal("zh-TW", reopened.GetSetting(SettingKeys.Language, ""));
        Assert.True(File.Exists(newBackground));
        Assert.False(File.Exists(Path.Combine(next.AppDataPath, "settings.json")));
        Assert.False(File.Exists(background));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".migration-*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
