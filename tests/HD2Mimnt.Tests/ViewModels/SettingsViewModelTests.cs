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
