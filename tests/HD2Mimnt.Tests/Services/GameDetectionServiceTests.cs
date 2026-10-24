using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;

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

    [Fact]
    public async Task ApplyConfiguredGameAsync_WithHellForgeMode_ShouldUseGameSubdirectory()
    {
        var root = CreateHellForgeInstallation();
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GameMode] = GameInstallationMode.HellForge.ToString(),
            [SettingKeys.HellForgePath] = root,
            [SettingKeys.GamePath] = Path.Combine(_rootDirectory, "Helldivers 2")
        });
        var service = new GameDetectionService(_settingsService);

        var result = await service.ApplyConfiguredGameAsync();

        Assert.True(result);
        Assert.Equal(GameStatus.Found, service.CurrentGameInfo?.Status);
        Assert.Equal(GameInstallationMode.HellForge, service.CurrentGameInfo?.Source);
        Assert.Equal(Path.Combine(root, "game"), service.CurrentGameInfo?.Path);
        Assert.Equal(Path.Combine(root, "game"), _settingsService.GetEffectiveGamePath());
    }

    [Fact]
    public async Task ValidateGameInstallationAsync_WithHellForgeMode_ShouldAcceptRootAndGameFolder()
    {
        var root = CreateHellForgeInstallation();
        var service = new GameDetectionService(_settingsService);

        Assert.True(await service.ValidateGameInstallationAsync(GameInstallationMode.HellForge, root));
        Assert.True(await service.ValidateGameInstallationAsync(
            GameInstallationMode.HellForge, Path.Combine(root, "game")));
        Assert.False(await service.ValidateGameInstallationAsync(
            GameInstallationMode.HellForge, Path.Combine(_rootDirectory, "missing")));
    }

    [Fact]
    public async Task SavePreferencesAsync_WithHellForgeMode_ShouldIgnoreInactiveOfficialPath()
    {
        var root = CreateHellForgeInstallation();
        var service = new GameDetectionService(_settingsService);

        await service.SavePreferencesAsync(
            GameInstallationMode.HellForge, "invalid-official-path", root, autoDetect: true);

        Assert.Equal(GameInstallationMode.HellForge, _settingsService.GetGameInstallationMode());
        Assert.Equal("invalid-official-path", _settingsService.GetSetting(SettingKeys.GamePath, string.Empty));
        Assert.Equal(root, _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
        Assert.Equal(Path.Combine(root, "game"), service.CurrentGameInfo?.Path);
        Assert.Equal(GameStatus.Found, service.CurrentGameInfo?.Status);
    }

    [Fact]
    public async Task SavePreferencesAsync_WithHellForgeGameFolder_ShouldNormalizeToInstallationRoot()
    {
        var root = CreateHellForgeInstallation();
        var service = new GameDetectionService(_settingsService);

        await service.SavePreferencesAsync(
            GameInstallationMode.HellForge, string.Empty, Path.Combine(root, "game"), autoDetect: false);

        Assert.Equal(root, _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
        Assert.Equal(Path.Combine(root, "game"), _settingsService.GetEffectiveGamePath());
        Assert.Equal(Path.Combine(root, "game"), service.CurrentGameInfo?.Path);
    }

    [Fact]
    public async Task SavePreferencesAsync_WithOfficialMode_ShouldIgnoreInactiveHellForgePath()
    {
        var official = Path.Combine(_rootDirectory, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(official, name));
        await File.WriteAllTextAsync(Path.Combine(official, "bin", "helldivers2.exe"), string.Empty);
        var service = new GameDetectionService(_settingsService);

        await service.SavePreferencesAsync(
            GameInstallationMode.Official, official, "invalid-hellforge-path", autoDetect: false);

        Assert.Equal(GameInstallationMode.Official, _settingsService.GetGameInstallationMode());
        Assert.Equal(official, _settingsService.GetSetting(SettingKeys.GamePath, string.Empty));
        Assert.Equal("invalid-hellforge-path", _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
        Assert.Equal(official, service.CurrentGameInfo?.Path);
        Assert.Equal(official, _settingsService.GetEffectiveGamePath());
    }

    [Fact]
    public async Task SavePreferencesAsync_WithInvalidActiveHellForgePath_ShouldKeepSavedMode()
    {
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GameMode] = GameInstallationMode.Official.ToString(),
            [SettingKeys.GamePath] = "saved-official"
        });
        var service = new GameDetectionService(_settingsService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SavePreferencesAsync(
            GameInstallationMode.HellForge, "saved-official", "missing", autoDetect: false));

        Assert.Equal(GameInstallationMode.Official, _settingsService.GetGameInstallationMode());
        Assert.Equal("saved-official", _settingsService.GetSetting(SettingKeys.GamePath, string.Empty));
        Assert.Equal(string.Empty, _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty));
        Assert.Equal(GameStatus.Invalid, service.CurrentGameInfo?.Status);
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

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
