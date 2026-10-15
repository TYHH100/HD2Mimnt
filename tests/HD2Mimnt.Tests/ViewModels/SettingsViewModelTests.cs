using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

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
        Assert.Contains("缓存", vm.SettingsCategories);
        Assert.Equal("Light", _settingsService.GetSetting(SettingKeys.Theme, "Dark"));
    }

    [Fact]
    public async Task ProfilePreferences_ShouldDefaultOffSaveWithoutGamePathAndReset()
    {
        var vm = CreateViewModel("invalid-game-path");
        await vm.LoadAsync();
        Assert.False(vm.AutoCheckConflicts);
        Assert.True(vm.LockLoaders);
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
        Assert.False(await reopened.GetSettingAsync(SettingKeys.ProfilesLockLoaders, true));
        await vm.LoadAsync();
        Assert.True(vm.AutoCheckConflicts);
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.False(vm.AutoCheckConflicts);
        Assert.False(applied);
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
