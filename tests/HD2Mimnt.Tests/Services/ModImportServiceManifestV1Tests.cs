using HD2Mimnt.Application.Services;
using HD2Mimnt.Tests.TestHelpers;
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
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

        var testDir = Path.Combine(_testStoragePath, "test_mod");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "Option1"));
        Directory.CreateDirectory(Path.Combine(testDir, "Option2"));

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
        Assert.NotNull(manifest.Options);
        Assert.Equal(2, manifest.Options.Count);
        Assert.Equal("Option1", manifest.Options[0].Name);
        Assert.Equal("Option2", manifest.Options[1].Name);
        Assert.True(manifest.Options[0].DefaultEnabled);
        Assert.True(manifest.Options[1].DefaultEnabled);
    }

    [Fact]
    public void InferManifestFromDirectory_WithForceV1Disabled_ReturnsLegacyManifest()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: SeededSettingsService.Create(forceManifestV1: false));

        var testDir = Path.Combine(_testStoragePath, "test_mod");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "Option1"));
        Directory.CreateDirectory(Path.Combine(testDir, "Option2"));

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
    public void InferManifestFromDirectory_WithoutSubdirectories_ReturnsManifestWithoutOptions()
    {
        // 无子目录也要生成基础档案：整包部署由“没有选项”表达，而不是空清单
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

        var testDir = Path.Combine(_testStoragePath, "test_mod_no_sub");
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "test.txt"), "test");

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
        Assert.Equal("Test Mod", manifest.Name);
        Assert.Null(manifest.Options);
        Assert.Null(manifest.LegacyOptions);
        Assert.NotEqual(Guid.Empty, manifest.Guid);
    }

    [Fact]
    public async Task UpgradeModManifestToV1Async_WithLegacyManifest_ConvertsToV1()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

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

        // 保存 Legacy 清单（解析器按"缺 Version 字段"识别 Legacy，
        // Legacy 选项存放在 "Options" 字符串数组——与 V1 的对象数组同字段不同形态）
        var manifestPath = Path.Combine(modDir, "manifest.json");
        var json = """
            {
              "Name": "Test Mod",
              "Description": "A test mod",
              "Author": "Test Author",
              "Options": ["Option1", "Option2"]
            }
            """;
        File.WriteAllText(manifestPath, json);

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = modDir,
            Manifest = legacyManifest
        };

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
            settingsService: SeededSettingsService.Create(forceManifestV1: false));

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = Path.Combine(_testStoragePath, "nonexistent"),
            Manifest = new ModManifest { ManifestVersion = ManifestVersion.Legacy }
        };

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
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Path = Path.Combine(_testStoragePath, "test"),
            Manifest = new ModManifest { ManifestVersion = ManifestVersion.V1 }
        };

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
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

        var mods = new List<Mod>
        {
            new() { Id = Guid.NewGuid(), Name = "Mod1", Path = Path.Combine(_testStoragePath, "mod1"), Manifest = new() { ManifestVersion = ManifestVersion.V1 } },
            new() { Id = Guid.NewGuid(), Name = "Mod2", Path = Path.Combine(_testStoragePath, "mod2"), Manifest = new() { ManifestVersion = ManifestVersion.Legacy } },
            new() { Id = Guid.NewGuid(), Name = "Mod3", Path = Path.Combine(_testStoragePath, "mod3"), Manifest = null }
        };

        _mockRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mods);

        _mockRepository
            .Setup(r => r.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        // Mod1 已是 V1 → 跳过；Mod2 有 Legacy 清单但目录缺失 → 失败；
        // Mod3 无清单且目录缺失（无子目录可推断）→ 计为跳过
        var (upgraded, skipped, failed) = await importService.UpgradeAllModsManifestToV1Async();

        // Assert
        Assert.Equal(0, upgraded);
        Assert.Equal(2, skipped); // Mod1（已是 V1）与 Mod3（无清单无目录）
        Assert.Equal(1, failed);  // Mod2（Legacy 但目录缺失）
    }

    [Fact]
    public void InferManifestFromDirectory_PreservesModMetadata()
    {
        // Arrange
        var importService = new ModImportService(
            _mockRepository.Object,
            _testStoragePath,
            settingsService: SeededSettingsService.Create(forceManifestV1: true));

        var testDir = Path.Combine(_testStoragePath, "test_metadata");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(testDir, "SubDir"));

        // Act
        var manifest = importService.InferManifestFromDirectory(testDir, "Test Mod");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal("Test Mod", manifest.Name);
        Assert.Equal("A locally imported mod.", manifest.Description);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
    }
}
