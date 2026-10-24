using System.Globalization;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Localization;
using HD2Mimnt.Infrastructure.Localization;
using Moq;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Tests.Services;

public sealed class LocalizationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-language-{Guid.NewGuid():N}");
    private readonly SettingsService _settings;
    private readonly Mock<ISystemLanguageProvider> _system = new();
    private readonly TranslationCatalog _catalog = new();
    private CultureInfo _systemCulture = CultureInfo.GetCultureInfo("zh-CN");

    public LocalizationServiceTests()
    {
        _settings = new SettingsService(_root);
        _system.Setup(provider => provider.GetCurrentCulture()).Returns(() => _systemCulture);
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("en-GB", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("ja-JP", "en-US")]
    [InlineData("", "en-US")]
    public void Constructor_Automatic_ShouldMatchSystemDisplayLanguage(string system, string expected)
    {
        _systemCulture = CultureInfo.GetCultureInfo(system);
        using var service = CreateService();

        Assert.True(service.IsAutomatic);
        Assert.Equal(LanguageCodes.Automatic, service.SelectedLanguage);
        Assert.Equal(expected, service.CurrentLanguage);
        Assert.Equal(new[] { "zh-CN", "zh-TW", "en-US" }, service.SupportedLanguages.Select(item => item.Code));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Fact]
    public async Task SetLanguageAsync_ShouldPersistUpdateResourcesAndNotifyImmediately()
    {
        using var service = CreateService();
        var properties = new List<string?>();
        var events = new List<LanguageChangedEventArgs>();
        service.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        service.LanguageChanged += (_, args) =>
        {
            Assert.Equal("Save", service["Common.Save"]);
            events.Add(args);
        };

        await service.SetLanguageAsync(" EN-us ");

        Assert.Equal("en-US", service.SelectedLanguage);
        Assert.False(service.IsAutomatic);
        Assert.Equal("en-US", service.CurrentLanguage);
        Assert.Contains("Item[]", properties);
        var changed = Assert.Single(events);
        Assert.Equal("auto", changed.PreviousLanguage);
        Assert.Equal("zh-CN", changed.PreviousCulture.Name);
        Assert.Equal("en-US", changed.CurrentCulture.Name);
        using var reopened = new LocalizationService(new SettingsService(_root), _system.Object, _catalog);
        Assert.Equal("en-US", reopened.SelectedLanguage);
        Assert.Equal("Save", reopened["Common.Save"]);

        properties.Clear();
        await service.SetLanguageAsync("en-US");
        Assert.Empty(properties);
        Assert.Single(events);
    }

    [Fact]
    public async Task RefreshSystemLanguage_ShouldFollowAutomaticAndPreserveManualChoice()
    {
        using var service = CreateService();
        _systemCulture = CultureInfo.GetCultureInfo("zh-HK");
        service.RefreshSystemLanguage();
        Assert.Equal("zh-TW", service.CurrentLanguage);
        Assert.Equal("儲存", service["Common.Save"]);

        await service.SetLanguageAsync("zh-CN");
        _systemCulture = CultureInfo.GetCultureInfo("en-GB");
        service.RefreshSystemLanguage();
        Assert.Equal("zh-CN", service.CurrentLanguage);
        await service.SetLanguageAsync("auto");
        Assert.Equal("en-US", service.CurrentLanguage);
        Assert.Equal("auto", await _settings.GetSettingAsync(SettingKeys.Language, string.Empty));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("")]
    [InlineData(null)]
    public async Task SetLanguageAsync_InvalidChoice_ShouldRejectWithoutChangingState(string? language)
    {
        using var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLanguageAsync(language!));
        Assert.True(service.IsAutomatic);
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }

    [Theory]
    [InlineData("removed-language")]
    [InlineData(42)]
    [InlineData(null)]
    public async Task Constructor_InvalidSavedChoice_ShouldUseAutomatic(object? language)
    {
        await _settings.SetSettingAsync(SettingKeys.Language, language);
        using var service = CreateService();
        Assert.True(service.IsAutomatic);
        Assert.Equal("zh-CN", service.CurrentLanguage);
    }

    [Fact]
    public async Task SettingsChanges_ResetsAndRestore_ShouldApplyWithoutRestart()
    {
        using var service = CreateService();
        await _settings.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.Language] = "en-US", [SettingKeys.Theme] = "Light"
        });
        Assert.Equal("en-US", service.CurrentLanguage);

        await _settings.ResetCategoryAsync("Download");
        Assert.Equal("en-US", service.CurrentLanguage);
        await _settings.ResetCategoryAsync("UI");
        Assert.True(service.IsAutomatic);
        Assert.Equal("zh-CN", service.CurrentLanguage);

        var restoredSettings = new SettingsService(_root);
        await restoredSettings.SetSettingAsync(SettingKeys.Language, "zh-TW");
        await _settings.ReloadAsync();
        Assert.Equal("zh-TW", service.CurrentLanguage);

        await restoredSettings.ResetToDefaultAsync();
        await _settings.ReloadAsync();
        Assert.True(service.IsAutomatic);
        await service.SetLanguageAsync("en-US");
        await _settings.ResetToDefaultAsync();
        Assert.Equal("zh-CN", service.CurrentLanguage);
    }

    [Fact]
    public async Task SetLanguageAsync_WriteFailure_ShouldKeepLanguageAndNotNotify()
    {
        using var service = CreateService();
        await service.SetLanguageAsync("zh-TW");
        var eventCount = 0;
        service.LanguageChanged += (_, _) => eventCount++;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root, "hd2mimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_settings BEFORE UPDATE ON Settings BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();

        var failure = await Record.ExceptionAsync(() => service.SetLanguageAsync("en-US"));
        Assert.IsType<SqliteException>(failure);

        Assert.Equal("zh-TW", service.CurrentLanguage);
        Assert.Equal("儲存", service["Common.Save"]);
        Assert.Equal("zh-TW", await _settings.GetSettingAsync(SettingKeys.Language, string.Empty));
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task Format_BackgroundLookup_ShouldUseSelectedCultureWithoutMutatingThreadCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var service = CreateService();
            await service.SetLanguageAsync("en-US");
            Assert.Equal("1,234 items", await Task.Run(() => service.Format("Common.ItemCount", 1234)));
            Assert.Equal("Failed to save settings: disk", service.Format("Settings.SaveFailed", "disk"));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("fr-FR", CultureInfo.CurrentUICulture.Name);
            Assert.Same(defaultCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Same(defaultUiCulture, CultureInfo.DefaultThreadCurrentUICulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void TranslationCatalog_UnknownCultureAndKey_ShouldFallBackToEnglishThenKey()
    {
        Assert.Equal("Save", _catalog.GetString("Common.Save", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("missing.key", _catalog.GetString("missing.key", CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Throws<ArgumentException>(() => _catalog.GetString("", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task StartMonitoring_ShouldDetectSystemChangesAndDisposeCleanly()
    {
        using var service = CreateService(TimeSpan.FromMilliseconds(20));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LanguageChanged += (_, _) => changed.TrySetResult();
        _systemCulture = CultureInfo.GetCultureInfo("zh-HK");
        service.StartMonitoring();
        service.StartMonitoring();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("zh-TW", service.CurrentLanguage);

        service.Dispose();
        await _settings.SetSettingAsync(SettingKeys.Language, "en-US");
        Assert.Equal("zh-TW", service.CurrentLanguage);
        Assert.Throws<ObjectDisposedException>(() => service.StartMonitoring());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetLanguageAsync("en-US"));
    }

    [Fact]
    public void Constructor_SystemDetectionFailure_ShouldFallBackWithoutFailingStartup()
    {
        _system.Setup(provider => provider.GetCurrentCulture()).Throws(new InvalidOperationException());
        using var service = CreateService();
        Assert.True(service.IsAutomatic);
        Assert.Equal("en-US", service.CurrentLanguage);
    }

    [Fact]
    public void SystemLanguageProvider_ShouldReturnAValidDisplayCulture()
    {
        Assert.NotNull(new SystemLanguageProvider().GetCurrentCulture());
    }

    private LocalizationService CreateService(TimeSpan? interval = null)
        => new(_settings, _system.Object, _catalog, monitorInterval: interval);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
