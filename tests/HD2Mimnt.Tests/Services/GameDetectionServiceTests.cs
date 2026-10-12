using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class GameDetectionServiceTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly SettingsService _settingsService;

    public GameDetectionServiceTests()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"hd2mimnt-game-detection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);
        _settingsService = new SettingsService(_rootDirectory);
    }

    [Fact]
    public async Task ApplyConfiguredPathAsync_WithValidPath_ShouldUseOnlyConfiguredPath()
    {
        var gameDirectory = Path.Combine(_rootDirectory, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(gameDirectory, name));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "bin", "helldivers2.exe"), string.Empty);
        var service = new GameDetectionService(_settingsService);

        var result = await service.ApplyConfiguredPathAsync(gameDirectory);

        Assert.True(result);
        Assert.Equal(GameStatus.Found, service.CurrentGameInfo?.Status);
        Assert.Equal(gameDirectory, service.CurrentGameInfo?.Path);
    }

    [Fact]
    public async Task ApplyConfiguredPathAsync_WithEmptyPath_ShouldNotScanAndSetNotFound()
    {
        var service = new GameDetectionService(_settingsService);

        var result = await service.ApplyConfiguredPathAsync(string.Empty);

        Assert.False(result);
        Assert.Equal(GameStatus.NotFound, service.CurrentGameInfo?.Status);
        Assert.Equal(string.Empty, service.CurrentGameInfo?.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
