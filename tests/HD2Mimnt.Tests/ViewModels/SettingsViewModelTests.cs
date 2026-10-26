using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly SettingsService _settingsService;
    private readonly GameDetectionService _gameDetectionService;
    private readonly Mock<IImageCacheService> _imageCache = new();

    public SettingsViewModelTests()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"hd2mimnt-settings-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);
        _settingsService = new SettingsService(_rootDirectory);
        _gameDetectionService = new GameDetectionService(_settingsService);
        _imageCache.Setup(service => service.GetStats()).Returns(new ImageCacheStats());
    }

    [Fact]
    public async Task GameCategory_FailedSecondSetting_ShouldKeepOldDatabaseAndRuntimeState()
    {
        var game = Path.Combine(_rootDirectory, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" }) Directory.CreateDirectory(Path.Combine(game, name));
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "helldivers2.exe"), string.Empty);
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        { [SettingKeys.GamePath] = "previous-game", [SettingKeys.GameAutoDetect] = true });
        await _gameDetectionService.ApplyConfiguredPathAsync("previous-game");
        using var vm = CreateViewModel(game);
        vm.AutoDetectGame = false;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_rootDirectory, "darktidemimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_game BEFORE UPDATE ON Settings WHEN NEW.Key = 'Game.AutoDetect' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("保存设置失败", vm.StatusMessage);
        Assert.Equal("previous-game", _settingsService.GetSetting(SettingKeys.GamePath, ""));
        Assert.Equal("previous-game", _gameDetectionService.CurrentGameInfo!.Path);
        Assert.True(_settingsService.GetSetting(SettingKeys.GameAutoDetect, false));
    }

    [Fact]
    public async Task AppearanceCategory_ShouldSaveWithoutOtherValidationAndResetOnlyAppearance()
    {
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GamePath] = "saved-game",
            [SettingKeys.ProfilesLockLoaders] = false,
            [SettingKeys.DownloadParallelSegments] = 8,
            [SettingKeys.HiddenEntries] = new[] { "settings.cache" }
        });
        using var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync();
        vm.SelectedCategory = "外观";
        vm.GamePath = "invalid-game-path";
        vm.DownloadParallelSegments = 0;
        vm.Theme = "Light";
        vm.CardOpacity = 70;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("设置已保存", vm.StatusMessage);
        Assert.Equal("Light", _settingsService.GetSetting(SettingKeys.Theme, "Dark"));
        Assert.Equal(70d, _settingsService.GetSetting(SettingKeys.CardOpacity, 100d));
        Assert.Equal("saved-game", _settingsService.GetSetting(SettingKeys.GamePath, ""));
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal("Dark", vm.Theme);
        Assert.Equal(100d, vm.CardOpacity);
        Assert.Equal("saved-game", _settingsService.GetSetting(SettingKeys.GamePath, ""));
        Assert.False(_settingsService.GetSetting(SettingKeys.ProfilesLockLoaders, true));
        Assert.Equal(8, _settingsService.GetSetting(SettingKeys.DownloadParallelSegments, 4));
        Assert.Equal(new[] { "settings.cache" }, _settingsService.GetSetting(SettingKeys.HiddenEntries, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("配置文件", "Deployment.DefaultMethod", "Profiles.LockLoaders")]
    [InlineData("模组库", "Search.Fuzzy", "Library.ImportAutoClose")]
    public async Task MergedCategory_FailedSave_ShouldRollBackAllSections(string category, string failureKey, string firstKey)
    {
        await _settingsService.SetSettingAsync(firstKey, false);
        using var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync(); vm.SelectedCategory = category;
        vm.LockLoaders = true; vm.ImportAutoClose = true; vm.SearchFuzzy = true;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_rootDirectory, "darktidemimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_save BEFORE INSERT ON Settings WHEN NEW.Key = '{failureKey}' BEGIN SELECT RAISE(ABORT, 'test save failure'); END";
        command.ExecuteNonQuery();
        var events = 0;
        _settingsService.SettingChanged += (_, _) => events++;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("保存设置失败", vm.StatusMessage);
        Assert.False(_settingsService.GetSetting(firstKey, true));
        Assert.False(new SettingsService(_rootDirectory).GetSetting(firstKey, true));
        Assert.Equal(0, events);
    }

    [Theory]
    [InlineData("配置文件", "Deployment.DefaultMethod", "Profiles.LockLoaders")]
    [InlineData("模组库", "Search.Fuzzy", "Library.ImportAutoClose")]
    public async Task MergedCategory_FailedReset_ShouldPreserveAllSectionsAndReportFailure(string category, string failureKey, string firstKey)
    {
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        { [firstKey] = true, [failureKey] = true });
        using var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync(); vm.SelectedCategory = category;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_rootDirectory, "darktidemimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_reset BEFORE DELETE ON Settings WHEN OLD.Key = '{failureKey}' BEGIN SELECT RAISE(ABORT, 'test reset failure'); END";
        command.ExecuteNonQuery();
        var events = 0;
        _settingsService.SettingChanged += (_, _) => events++;
        Assert.Null(await Record.ExceptionAsync(() => vm.ResetCommand.ExecuteAsync(null)));
        Assert.Contains("失败", vm.StatusMessage);
        Assert.True(_settingsService.GetSetting(firstKey, false));
        Assert.True(new SettingsService(_rootDirectory).GetSetting(firstKey, false));
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task BackgroundSelection_FailedWrite_ShouldKeepOldPathAndReportFailure()
    {
        await _settingsService.SetSettingAsync(SettingKeys.BackgroundImagePath, "previous.png");
        using var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_rootDirectory, "darktidemimnt.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_background BEFORE UPDATE ON Settings BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        var changed = 0;
        vm.AppearanceChanged += (_, _) => changed++;
        Assert.Null(await Record.ExceptionAsync(() => vm.SetBackgroundImagePathAsync("new.png")));
        Assert.Equal("previous.png", vm.BackgroundImagePath);
        Assert.Contains("失败", vm.StatusMessage);
        Assert.Null(await Record.ExceptionAsync(() => vm.ClearBackgroundImageCommand.ExecuteAsync(null)));
        Assert.Equal("previous.png", vm.BackgroundImagePath);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task MergedCategories_ShouldKeepSectionVisibilityAndLegacyEntryIds()
    {
        using var vm = CreateViewModel("invalid-game-path");
        Assert.Equal(10, vm.SettingsCategories.Count());
        foreach (var (previous, current) in new[] { ("部署", "配置文件"), ("搜索", "模组库"), ("缓存", "存储位置") })
        {
            Assert.DoesNotContain(previous, vm.SettingsCategories);
            vm.SelectedCategory = previous;
            Assert.Equal(current, vm.SelectedCategory);
        }
        vm.SelectedCategory = "模组库";
        Assert.True(vm.IsLibraryCategoryVisible);
        Assert.True(vm.IsSearchCategoryVisible);
        await _settingsService.SetSettingAsync(SettingKeys.HiddenEntries, new[] { "settings.library" });
        await vm.LoadAsync();
        Assert.Equal("模组库", vm.SelectedCategory);
        Assert.False(vm.IsLibraryCategoryVisible);
        Assert.True(vm.IsSearchCategoryVisible);
        await _settingsService.SetSettingAsync(SettingKeys.HiddenEntries, new[] { "settings.library", "settings.search" });
        await vm.LoadAsync();
        Assert.DoesNotContain("模组库", vm.SettingsCategories);
        Assert.Equal("入口自定义", vm.SelectedCategory);
    }

    [Theory]
    [InlineData("配置文件")]
    [InlineData("模组库")]
    public async Task MergedCategory_ShouldSaveBothSectionsAndResetOnlyItsSettings(string category)
    {
        await _settingsService.SetSettingAsync(SettingKeys.Theme, "Light");
        await _settingsService.SetSettingAsync(SettingKeys.DownloadParallelSegments, 8);
        using var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync();
        vm.SelectedCategory = category;
        vm.AutoAddImportedMods = true;
        vm.DefaultDeploymentMethod = HD2Mimnt.Core.Enums.DeploymentMethod.HardLink;
        vm.DeployBottomToTop = true;
        vm.CheckBeforeDeploy = false;
        vm.ImportAutoClose = true;
        vm.SearchFuzzy = true;
        vm.SearchPinyin = true;
        await vm.SaveCommand.ExecuteAsync(null);
        var saved = new SettingsService(_rootDirectory);
        if (category == "配置文件")
        {
            Assert.True(saved.GetSetting(SettingKeys.ProfilesAutoAddImportedMods, false));
            Assert.Equal("HardLink", saved.GetSetting(SettingKeys.DefaultDeploymentMethod, ""));
            Assert.Equal("BottomToTop", saved.GetSetting(SettingKeys.DefaultDeploymentOrder, ""));
            Assert.False(saved.GetSetting(SettingKeys.DeploymentCheckBeforeDeploy, true));
            Assert.False(saved.GetSetting(SettingKeys.ImportAutoClose, false));
        }
        else
        {
            Assert.True(saved.GetSetting(SettingKeys.ImportAutoClose, false));
            Assert.True(saved.GetSetting(SettingKeys.SearchFuzzy, false));
            Assert.True(saved.GetSetting(SettingKeys.SearchPinyin, false));
            Assert.False(saved.GetSetting(SettingKeys.ProfilesAutoAddImportedMods, false));
        }
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.False(vm.AutoAddImportedMods);
        Assert.Equal(HD2Mimnt.Core.Enums.DeploymentMethod.Copy, vm.DefaultDeploymentMethod);
        Assert.False(vm.ImportAutoClose);
        Assert.False(vm.SearchFuzzy);
        Assert.False(vm.SearchPinyin);
        Assert.Equal("Light", _settingsService.GetSetting(SettingKeys.Theme, "Dark"));
        Assert.Equal(8, _settingsService.GetSetting(SettingKeys.DownloadParallelSegments, 4));
    }

    [Fact]
    public async Task CompatibilityCategory_SavesWithoutGamePathPersistsAndResetsOnlyItsOwnSettings()
    {
        await _settingsService.SetSettingAsync(SettingKeys.Theme, "Light");
        using var vm = CreateViewModel("Z:/missing/game");
        await vm.LoadAsync(); vm.SelectedCategory = "兼容性";
        Assert.Contains("兼容性", vm.SettingsCategories); Assert.True(vm.IsCompatibilityCategoryVisible);
        Assert.False(vm.CompatibilityCheckOnStartup); Assert.False(vm.CompatibilityEnableBatchRepair);
        vm.CompatibilityCheckOnStartup = true; vm.CompatibilityEnableBatchRepair = true;
        await vm.SaveCommand.ExecuteAsync(null);
        var reopened = new SettingsService(_rootDirectory);
        Assert.True(reopened.GetSetting(SettingKeys.CompatibilityCheckOnStartup, false));
        Assert.True(reopened.GetSetting(SettingKeys.CompatibilityEnableBatchRepair, false));
        using var reloaded = new SettingsViewModel(reopened, _gameDetectionService, new NotificationService(), _imageCache.Object);
        await reloaded.LoadAsync(); Assert.True(reloaded.CompatibilityCheckOnStartup); Assert.True(reloaded.CompatibilityEnableBatchRepair);
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.False(vm.CompatibilityCheckOnStartup); Assert.False(vm.CompatibilityEnableBatchRepair);
        Assert.Equal("Light", _settingsService.GetSetting(SettingKeys.Theme, "Dark"));
    }

    [Fact]
    public async Task EntryVisibility_ShouldSaveIndependentlyPersistAndResetOnlyEntries()
    {
        using var vm = CreateViewModel("invalid-game-path");
        vm.SelectedCategory = "入口自定义";
        await _settingsService.SetSettingAsync(SettingKeys.Theme, "Light");
        foreach (var entry in vm.NavigationEntries.Concat(vm.SettingsEntries)) entry.IsVisible = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.ShowBrowseEntry);
        Assert.Equal(new[] { "入口自定义" }, vm.SettingsCategories);
        Assert.True(vm.IsEntriesCategoryVisible);
        var saved = new SettingsService(_rootDirectory);
        Assert.Equal(vm.NavigationEntries.Count + vm.SettingsEntries.Count, saved.GetSetting(SettingKeys.HiddenEntries, Array.Empty<string>()).Length);
        using var reopened = new SettingsViewModel(saved, _gameDetectionService, new NotificationService(), _imageCache.Object);
        Assert.False(reopened.ShowProfilesEntry);
        Assert.False(reopened.ShowToolsEntry);
        Assert.Equal("入口自定义", reopened.SelectedCategory);

        await vm.ResetCommand.ExecuteAsync(null);
        Assert.All(vm.NavigationEntries.Concat(vm.SettingsEntries), entry => Assert.True(entry.IsVisible));
        Assert.True(vm.ShowBrowseEntry);
        Assert.Contains("存储位置", vm.SettingsCategories);
        Assert.Equal("Light", _settingsService.GetSetting(SettingKeys.Theme, "Dark"));
    }

    [Fact]
    public async Task ProfilePreferences_ShouldDefaultOffSaveWithoutGamePathAndReset()
    {
        var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync();
        Assert.False(vm.AutoCheckConflicts);
        Assert.True(vm.LockLoaders);
        Assert.False(vm.AutoAddImportedMods);
        vm.AutoAddImportedMods = true;
        vm.GamePath = "invalid-game-path";
        vm.SelectedCategory = "配置文件";
        vm.AutoCheckConflicts = true;
        vm.LockLoaders = false;
        bool? applied = null;
        vm.AutoCheckConflictsChanged += (_, enabled) => applied = enabled;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(applied);
        Assert.Equal("配置文件设置已保存", vm.StatusMessage);
        var reopened = new SettingsService(_rootDirectory);
        Assert.True(await reopened.GetSettingAsync(SettingKeys.ProfilesAutoCheckConflicts, false));
        Assert.True(await reopened.GetSettingAsync(SettingKeys.ProfilesAutoAddImportedMods, false));
        Assert.False(await reopened.GetSettingAsync(SettingKeys.ProfilesLockLoaders, true));
        await vm.LoadAsync();
        Assert.True(vm.AutoCheckConflicts);
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.False(vm.AutoCheckConflicts);
        Assert.False(applied);
        Assert.False(vm.AutoAddImportedMods);
        Assert.True(vm.LockLoaders);
    }

    [Fact]
    public async Task SaveCommand_WithValidGamePath_ShouldPersistAndUpdateRuntimeDetection()
    {
        var gameDirectory = Path.Combine(_rootDirectory, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(gameDirectory, name));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "bin", "helldivers2.exe"), string.Empty);
        var viewModel = CreateViewModel(gameDirectory);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(GameStatus.Found, _gameDetectionService.CurrentGameInfo?.Status);
        Assert.Equal(gameDirectory, _gameDetectionService.CurrentGameInfo?.Path);
        Assert.Equal(gameDirectory, await _settingsService.GetSettingAsync(SettingKeys.GamePath, string.Empty));
    }

    [Fact]
    public async Task SaveCommand_WithInvalidGamePath_ShouldKeepRuntimeStateInvalid()
    {
        var invalidDirectory = Path.Combine(_rootDirectory, "invalid");
        Directory.CreateDirectory(invalidDirectory);
        var viewModel = CreateViewModel(invalidDirectory);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(GameStatus.Invalid, _gameDetectionService.CurrentGameInfo?.Status);
        Assert.Contains("保存设置失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await _settingsService.GetSettingAsync(SettingKeys.GamePath, string.Empty));
    }

    [Fact]
    public async Task GameCategory_HellForgeSource_ShouldSaveAndKeepOfficialPathForSwitchBack()
    {
        var official = CreateOfficialGameDirectory();
        var hellForge = CreateHellForgeInstallation();
        using var vm = CreateViewModel(official);

        vm.IsOfficialGameSource = false;
        vm.HellForgePath = hellForge;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("设置已保存", vm.StatusMessage);
        Assert.Equal(HD2Mimnt.Core.Enums.GameInstallationMode.HellForge, _settingsService.GetGameInstallationMode());
        Assert.Equal(hellForge, _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
        Assert.Equal(official, _settingsService.GetSetting(SettingKeys.GamePath, string.Empty));
        Assert.Equal(Path.Combine(hellForge, "game"), _gameDetectionService.CurrentGameInfo?.Path);

        vm.IsOfficialGameSource = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(HD2Mimnt.Core.Enums.GameInstallationMode.Official, _settingsService.GetGameInstallationMode());
        Assert.Equal(official, _gameDetectionService.CurrentGameInfo?.Path);
    }

    [Fact]
    public async Task LoadAsync_WithHellForgeMode_ShouldLoadBothPathsAndSource()
    {
        var official = CreateOfficialGameDirectory();
        var hellForge = CreateHellForgeInstallation();
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GameMode] = HD2Mimnt.Core.Enums.GameInstallationMode.HellForge.ToString(),
            [SettingKeys.GamePath] = official,
            [SettingKeys.HellForgePath] = hellForge
        });
        using var vm = CreateViewModel(string.Empty);

        await vm.LoadAsync();

        Assert.False(vm.IsOfficialGameSource);
        Assert.True(vm.IsHellForgeGameSource);
        Assert.Equal(official, vm.GamePath);
        Assert.Equal(hellForge, vm.HellForgePath);
    }

    [Fact]
    public async Task GameCategory_InvalidHellForgePath_ShouldKeepSavedOfficialMode()
    {
        var official = CreateOfficialGameDirectory();
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GameMode] = HD2Mimnt.Core.Enums.GameInstallationMode.Official.ToString(),
            [SettingKeys.GamePath] = official,
            [SettingKeys.GameAutoDetect] = false
        });
        using var vm = CreateViewModel(official);
        await vm.LoadAsync();

        vm.IsOfficialGameSource = false;
        vm.HellForgePath = Path.Combine(_rootDirectory, "missing");
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("保存设置失败", vm.StatusMessage);
        Assert.Equal(HD2Mimnt.Core.Enums.GameInstallationMode.Official, _settingsService.GetGameInstallationMode());
        Assert.Equal(official, _settingsService.GetSetting(SettingKeys.GamePath, string.Empty));
        Assert.Equal(string.Empty, _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
    }

    [Fact]
    public async Task DownloadCategory_Should_Save_Independently_And_Reload()
    {
        var viewModel = CreateViewModel("invalid-game-path");
        viewModel.SelectedCategory = "下载";
        viewModel.DownloadParallelSegments = 8;
        viewModel.DownloadSegmentSizeMiB = 16;
        viewModel.DownloadMaxRetries = 5;
        viewModel.DownloadRetryDelayMs = 1200;
        viewModel.DownloadEnableResume = false;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal("下载设置已保存，下次下载生效", viewModel.StatusMessage);
        Assert.Equal(string.Empty, await _settingsService.GetSettingAsync(SettingKeys.GamePath, string.Empty));
        var reopened = new DownloadSettingsService(new SettingsService(_rootDirectory));
        var options = await reopened.GetOptionsAsync();
        Assert.Equal(8, options.MaxParallelSegments);
        Assert.Equal(16 * 1024 * 1024, options.MinimumSegmentSize);
        Assert.Equal(5, options.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), options.RetryDelay);
        Assert.False(options.EnableResume);
        var reloaded = CreateViewModel("");
        await reloaded.LoadAsync();
        Assert.Equal(8, reloaded.DownloadParallelSegments);
        Assert.Equal(16, reloaded.DownloadSegmentSizeMiB);
        Assert.Equal(5, reloaded.DownloadMaxRetries);
        Assert.Equal(1200, reloaded.DownloadRetryDelayMs);
        Assert.False(reloaded.DownloadEnableResume);
    }

    [Fact]
    public async Task DownloadCategory_Reset_Should_Keep_Other_Settings()
    {
        await _settingsService.SetSettingAsync(SettingKeys.Theme, "Light");
        await new DownloadSettingsService(_settingsService).SaveAsync(new DownloadOptions
        {
            MaxParallelSegments = 9, MinimumSegmentSize = 8 * 1024 * 1024, EnableResume = false
        });
        var vm = CreateViewModel("");
        vm.SelectedCategory = "下载";
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.DownloadParallelSegments);
        Assert.Equal(4, vm.DownloadSegmentSizeMiB);
        Assert.Equal(2, vm.DownloadMaxRetries);
        Assert.Equal(500, vm.DownloadRetryDelayMs);
        Assert.True(vm.DownloadEnableResume);
        Assert.Equal("Light", await _settingsService.GetSettingAsync(SettingKeys.Theme, "Dark"));
    }

    [Theory]
    [InlineData(0, 4, 2, 500)]
    [InlineData(4, 0, 2, 500)]
    [InlineData(4, 257, 2, 500)]
    [InlineData(4, 4, 11, 500)]
    [InlineData(4, 4, 2, -1)]
    public async Task DownloadCategory_Invalid_Values_Should_Not_Persist(int parallel, int size, int retries, int delay)
    {
        var vm = CreateViewModel("");
        vm.SelectedCategory = "下载";
        vm.DownloadParallelSegments = parallel;
        vm.DownloadSegmentSizeMiB = size;
        vm.DownloadMaxRetries = retries;
        vm.DownloadRetryDelayMs = delay;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("保存设置失败", vm.StatusMessage);
        Assert.Empty(await _settingsService.GetAllSettingsAsync());
    }

    private string CreateOfficialGameDirectory()
    {
        var official = Path.Combine(_rootDirectory, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(official, name));
        File.WriteAllText(Path.Combine(official, "bin", "helldivers2.exe"), string.Empty);
        return official;
    }

    private string CreateHellForgeInstallation()
    {
        var root = Path.Combine(_rootDirectory, "HellForge");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hellforge-install.json"), "{}");
        var game = Path.Combine(root, "game");
        foreach (var name in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(game, name));
        File.WriteAllText(Path.Combine(game, "bin", "helldivers2.exe"), string.Empty);
        return root;
    }

    private SettingsViewModel CreateViewModel(string gamePath)
    {
        var viewModel = new SettingsViewModel(
            _settingsService,
            _gameDetectionService,
            new NotificationService(),
            _imageCache.Object)
        {
            GamePath = gamePath
        };
        return viewModel;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
