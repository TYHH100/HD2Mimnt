using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
    public async Task ReloadAsync_UnchangedDatabaseValues_ShouldNotNotify()
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
    public async Task Constructor_LegacyJson_ShouldMigrateTypesAndKeepExistingDatabaseValues()
    {
        Directory.CreateDirectory(_root);
        var repository = new SettingsRepository(Path.Combine(_root, "hd2mimnt.db"));
        await repository.ApplyAsync(new Dictionary<string, string> { [SettingKeys.Theme] = "\"Dark\"" }, Array.Empty<string>());
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, """
            { "UI.Theme": "Light", "Game.Path": "C:\\Games\\HD2", "Download.ParallelSegments": 8,
              "UI.CardOpacity": 49.5, "Download.EnableResume": false, "Extension.Options": { "items": [1, 2] } }
            """);

        var service = new SettingsService(_root);

        Assert.Equal("Dark", service.GetSetting(SettingKeys.Theme, ""));
        Assert.Equal(@"C:\Games\HD2", service.GetSetting(SettingKeys.GamePath, ""));
        Assert.Equal(8, service.GetSetting(SettingKeys.DownloadParallelSegments, 0));
        Assert.Equal(49.5, service.GetSetting(SettingKeys.CardOpacity, 0d));
        Assert.False(service.GetSetting(SettingKeys.DownloadEnableResume, true));
        Assert.Equal(2, service.GetSetting("Extension.Options", default(JsonElement)).GetProperty("items").GetArrayLength());
        Assert.False(File.Exists(path));
        Assert.True(repository.IsLegacyMigrationComplete());
        Assert.Equal(6, repository.ReadAll().Count);
        Assert.Equal(8, new SettingsService(_root).GetSetting(SettingKeys.DownloadParallelSegments, 0));
    }

    [Fact]
    public async Task ResetToDefaultAsync_ResidualJson_ShouldNotResurrectRemovedSettings()
    {
        var service = new SettingsService(_root);
        await service.SetSettingAsync(SettingKeys.Language, "zh-TW");
        // 模拟迁移后旧文件未能删除，或被旧版程序重新创建。
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{\"UI.Language\":\"en-US\"}");
        await service.ResetToDefaultAsync();

        var reopened = new SettingsService(_root);

        Assert.Equal("auto", reopened.GetSetting(SettingKeys.Language, "auto"));
        Assert.Empty(await reopened.GetAllSettingsAsync());
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Fact]
    public async Task Constructor_InvalidLegacyJson_ShouldPreserveFileAndAllowRetry()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{broken");

        Assert.Throws<JsonException>(() => new SettingsService(_root));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        var repository = new SettingsRepository(Path.Combine(_root, "hd2mimnt.db"));
        Assert.False(repository.IsLegacyMigrationComplete());
        Assert.Empty(repository.ReadAll());

        await File.WriteAllTextAsync(path, "{\"UI.Theme\":\"Light\"}");
        Assert.Equal("Light", new SettingsService(_root).GetSetting(SettingKeys.Theme, "Dark"));
    }

    [Fact]
    public async Task SetSettingsAsync_FailedTransaction_ShouldRollBackDatabaseMemoryAndNotifications()
    {
        var service = new SettingsService(_root);
        await service.SetSettingAsync(SettingKeys.Theme, "Dark");
        var changes = 0;
        service.SettingChanged += (_, _) => changes++;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root, "hd2mimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_settings BEFORE INSERT ON Settings WHEN NEW.Key = 'Test.Fail' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<SqliteException>(() => service.SetSettingsAsync(new Dictionary<string, object>
        { [SettingKeys.Theme] = "Light", ["Test.Fail"] = true }));

        Assert.Equal("Dark", service.GetSetting(SettingKeys.Theme, ""));
        Assert.Equal("Dark", new SettingsService(_root).GetSetting(SettingKeys.Theme, ""));
        Assert.Equal(0, changes);
        Assert.False(service.GetSetting("Test.Fail", false));
    }

    [Fact]
    public async Task SetSettingAsync_SeparateInstances_ShouldKeepUnrelatedKeys()
    {
        var first = new SettingsService(_root);
        var second = new SettingsService(_root);
        await first.SetSettingAsync(SettingKeys.Theme, "Light");
        await second.SetSettingAsync(SettingKeys.Language, "zh-TW");

        await first.ReloadAsync();

        Assert.Equal("Light", first.GetSetting(SettingKeys.Theme, ""));
        Assert.Equal("zh-TW", first.GetSetting(SettingKeys.Language, ""));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetAsync_SeparateInstanceKeys_ShouldResetPersistedValues(bool resetAll)
    {
        var service = new SettingsService(_root);
        var external = new SettingsService(_root);
        await external.SetSettingsAsync(new Dictionary<string, object>
        { [SettingKeys.Language] = "zh-TW", [SettingKeys.GamePath] = "game", ["UIX.Value"] = true });

        if (resetAll) await service.ResetToDefaultAsync();
        else await service.ResetCategoryAsync("ui");

        var reopened = new SettingsService(_root);
        Assert.Equal("auto", reopened.GetSetting(SettingKeys.Language, "auto"));
        Assert.Equal(resetAll ? "" : "game", reopened.GetSetting(SettingKeys.GamePath, ""));
        Assert.Equal(!resetAll, reopened.GetSetting("UIX.Value", false));
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
