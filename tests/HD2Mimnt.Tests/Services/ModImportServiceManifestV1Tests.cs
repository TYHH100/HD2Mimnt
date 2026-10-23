using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 测试 ModImportService 中的 ForceManifestV1 功能
/// </summary>
public class ModImportServiceManifestV1Tests
{
    private readonly Mock<IModRepository> _mockRepository = new();
    private readonly Mock<SettingsService> _mockSettingsService = new();
    private readonly string _testStoragePath;

    public ModImportServiceManifestV1Tests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"hd2mimnt_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
    }

    [Fact]
    public void InferManifestFromDirectory_WithForceV1Enabled_ReturnsV1Manifest()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var testDir = Path.Combine(_testStoragePath, "test_mod");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "Option1"));
        Directory.CreateDirectory(Path.Combine(testDir, "Option2"));

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
        Assert.NotNull(manifest.Options);
        Assert.Equal(2, manifest.Options.Count);
        Assert.Equal("Option1", manifest.Options[0].Name);
        Assert.Equal("Option2", manifest.Options[1].Name);
        Assert.False(manifest.Options[0].DefaultEnabled);
        Assert.False(manifest.Options[1].DefaultEnabled);
    }

    [Fact]
    public void InferManifestFromDirectory_WithForceV1Disabled_ReturnsLegacyManifest()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var testDir = Path.Combine(_testStoragePath, "test_mod");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "Option1"));
        Directory.CreateDirectory(Path.Combine(testDir, "Option2"));

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(false);

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.Legacy, manifest.ManifestVersion);
        Assert.NotNull(manifest.LegacyOptions);
        Assert.Equal(2, manifest.LegacyOptions.Count);
        Assert.Contains("Option1", manifest.LegacyOptions);
        Assert.Contains("Option2", manifest.LegacyOptions);
    }

    [Fact]
    public void InferManifestFromDirectory_WithNoSubdirectories_ReturnsNull()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var testDir = Path.Combine(_testStoragePath, "test_mod_no_sub");
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "test.txt"), "test");

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.Null(manifest);
    }

    [Fact]
    public async Task UpgradeModManifestToV1Async_WithLegacyManifest_ConvertsToV1()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var modDir = Path.Combine(_testStoragePath, "upgrade_test");
        Directory.CreateDirectory(modDir);

        var legacyManifest = new ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Test Mod",
            Description = "A test mod",
            Version = "1.0.0",
            Author = "Test Author",
            ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "Option1", "Option2" }
        };

        // 保存 Legacy 清单
        var manifestPath = Path.Combine(modDir, "manifest.json");
        var json = System.Text.Json.JsonSerializer.Serialize(legacyManifest);
        File.WriteAllText(manifestPath, json);

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = modDir,
            Manifest = legacyManifest
        };

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        _mockRepository
            .Setup(r => r.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await importService.UpgradeModManifestToV1Async(mod);

        // Assert
        Assert.True(result);
        Assert.NotNull(mod.Manifest);
        Assert.Equal(ManifestVersion.V1, mod.Manifest.ManifestVersion);
        Assert.NotNull(mod.Manifest.Options);
        Assert.Equal(2, mod.Manifest.Options.Count);
        Assert.Equal("Option1", mod.Manifest.Options[0].Name);
        Assert.Equal("Option2", mod.Manifest.Options[1].Name);
        _mockRepository.Verify(r => r.UpdateAsync(mod, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpgradeModManifestToV1Async_WithForceV1Disabled_ReturnsFalse()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = Path.Combine(_testStoragePath, "nonexistent"),
            Manifest = new ModManifest { ManifestVersion = ManifestVersion.Legacy }
        };

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(false);

        // Act
        var result = await importService.UpgradeModManifestToV1Async(mod);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task UpgradeModManifestToV1Async_WithAlreadyV1_ReturnsFalse()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = Path.Combine(_testStoragePath, "test"),
            Manifest = new ModManifest { ManifestVersion = ManifestVersion.V1 }
        };

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        // Act
        var result = await importService.UpgradeModManifestToV1Async(mod);

        // Assert
        Assert.False(result); // Already V1, no need to upgrade
    }

    [Fact]
    public async Task UpgradeAllModsManifestToV1Async_WithMultipleMods_UpgradesAll()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var mods = new List<Mod>
        {
            new() { Id = Guid.NewGuid(), Name = "Mod1", Path = Path.Combine(_testStoragePath, "mod1"), Manifest = new() { ManifestVersion = ManifestVersion.V1 } },
            new() { Id = Guid.NewGuid(), Name = "Mod2", Path = Path.Combine(_testStoragePath, "mod2"), Manifest = new() { ManifestVersion = ManifestVersion.Legacy } },
            new() { Id = Guid.NewGuid(), Name = "Mod3", Path = Path.Combine(_testStoragePath, "mod3"), Manifest = null }
        };

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        _mockRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mods);

        _mockRepository
            .Setup(r => r.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var (upgraded, skipped, failed) = await importService.UpgradeAllModsManifestToV1Async();

        // Assert
        Assert.Equal(0, upgraded); // Mod2 has no directory, Mod3 has no manifest - both skipped/failed
        Assert.Equal(1, skipped); // Mod1 is already V1
    }

    [Fact]
    public void InferManifestFromDirectory_PreservesModMetadata()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: _mockSettingsService.Object);

        var testDir = Path.Combine(_testStoragePath, "test_metadata");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "SubDir"));

        _mockSettingsService
            .Setup(s => s.GetSetting(SettingKeys.ForceManifestV1, It.IsAny<bool>()))
            .Returns(true);

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal("Test Mod", manifest.Name);
        Assert.Equal("A locally imported mod.", manifest.Description);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
    }
}
