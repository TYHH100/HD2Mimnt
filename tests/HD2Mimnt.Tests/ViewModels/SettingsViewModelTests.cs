using HD2Mimnt.Application.Services;
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
        var gameDirectory = Path.Combine(_rootDirectory, "game");
        Directory.CreateDirectory(gameDirectory);
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "helldivers2.exe"), string.Empty);
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
