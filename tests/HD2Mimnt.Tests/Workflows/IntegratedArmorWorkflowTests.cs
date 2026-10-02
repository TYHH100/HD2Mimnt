using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Workflows;

namespace HD2Mimnt.Tests.Workflows;

public class IntegratedArmorWorkflowTests : IDisposable
{
    private readonly IntegratedArmorWorkflow _workflow;
    private readonly string _testDirectory;

    public IntegratedArmorWorkflowTests()
    {
        _workflow = new IntegratedArmorWorkflow();
        _testDirectory = Path.Combine(Path.GetTempPath(), $"HD2MimntTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task CanHandleAsync_Should_Detect_Armor_Mod_With_Manifest()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "ArmorWithManifest");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), "{}");

        // Act
        var canHandle = await _workflow.CanHandleAsync(modPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Not_Detect_Non_Armor_Mod()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "NonArmorMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "readme.txt"), "Just a readme");

        // Act
        var canHandle = await _workflow.CanHandleAsync(modPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Detect_Armor_Mod_By_Structure()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "ArmorByStructure");
        Directory.CreateDirectory(modPath);
        Directory.CreateDirectory(Path.Combine(modPath, "armors"));

        // Act
        var canHandle = await _workflow.CanHandleAsync(modPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public async Task ParseManifest_Should_Parse_Valid_Manifest()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "ValidManifest");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""name"": ""Test Armor"",
            ""description"": ""Test Description"",
            ""author"": ""Test Author"",
            ""version"": ""1.0.0"",
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""name"": ""Heavy Armor"",
                    ""files"": [""armors/heavy.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        Directory.CreateDirectory(Path.Combine(modPath, "armors"));
        File.WriteAllText(Path.Combine(modPath, "armors", "heavy.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "armors", "heavy.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Parse_Gameplay_Armors()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "GameplayArmors");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""name"": ""Armor 1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                },
                {
                    ""id"": ""armor2"",
                    ""name"": ""Armor 2"",
                    ""files"": [""test2.patch_0""],
                    ""enabled"": false
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test.stream"), "data");
        File.WriteAllText(Path.Combine(modPath, "test2.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test2.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Parse_Appearance_Armors()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "AppearanceArmors");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""appearanceArmors"": [
                {
                    ""id"": ""app1"",
                    ""name"": ""Appearance 1"",
                    ""files"": [""appearance.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "appearance.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "appearance.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Parse_Body_Variants()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "BodyVariants");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [{""id"": ""a1"", ""files"": [""test.patch_0""], ""enabled"": true}],
            ""bodyVariants"": [
                {
                    ""id"": 1,
                    ""name"": ""Body Type A"",
                    ""files"": [""body1.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test.stream"), "data");
        File.WriteAllText(Path.Combine(modPath, "body1.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "body1.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Parse_Head_Variants()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "HeadVariants");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [{""id"": ""a1"", ""files"": [""test.patch_0""], ""enabled"": true}],
            ""headVariants"": [
                {
                    ""id"": 1,
                    ""name"": ""Head Type A"",
                    ""files"": [""head1.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test.stream"), "data");
        File.WriteAllText(Path.Combine(modPath, "head1.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "head1.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Handle_Missing_Optional_Fields()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "MinimalManifest");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_Should_Reject_Invalid_Manifest()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "InvalidManifest");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), "{ invalid json");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid JSON"));
    }

    [Fact]
    public async Task ValidateAsync_Should_Validate_All_Files_Present()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "AllFilesPresent");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test.stream"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_Should_Detect_Missing_Armor_Files()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "MissingFiles");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""missing.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing.patch_0"));
    }

    [Fact]
    public async Task ValidateAsync_Should_Detect_Missing_V1_Paired_Files()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "MissingPair");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        // Missing test.stream

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Missing paired .stream"));
    }

    [Fact]
    public async Task ValidateAsync_Should_Require_At_Least_One_Armor()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "NoArmors");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""name"": ""Empty Armor Mod""
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);

        var mod = new Mod { Path = modPath };

        // Act
        var result = await _workflow.ValidateAsync(modPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least one gameplay or appearance armor is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Generate_Files_For_Enabled_Gameplay_Armors()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "DeployGameplay");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                },
                {
                    ""id"": ""armor2"",
                    ""files"": [""test2.patch_0""],
                    ""enabled"": false
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "test2.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.Single(files);
        Assert.Contains(files, f => f.TargetRelativePath == "test.patch_0");
        Assert.DoesNotContain(files, f => f.TargetRelativePath == "test2.patch_0");
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Generate_Files_For_Enabled_Appearance_Armors()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "DeployAppearance");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""appearanceArmors"": [
                {
                    ""id"": ""app1"",
                    ""files"": [""appearance.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "appearance.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.Single(files);
        Assert.Contains(files, f => f.TargetRelativePath == "appearance.patch_0");
        Assert.Equal("Appearance", files[0].Metadata["ArmorType"]);
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Generate_Files_For_Enabled_Body_Variants()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "DeployBodyVariant");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [{""id"": ""a1"", ""files"": [""test.patch_0""], ""enabled"": true}],
            ""bodyVariants"": [
                {
                    ""id"": 1,
                    ""files"": [""body1.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "body1.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Metadata.ContainsKey("VariantType") && f.Metadata["VariantType"].ToString() == "Body");
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Generate_Files_For_Enabled_Head_Variants()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "DeployHeadVariant");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [{""id"": ""a1"", ""files"": [""test.patch_0""], ""enabled"": true}],
            ""headVariants"": [
                {
                    ""id"": 1,
                    ""files"": [""head1.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "head1.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Metadata.ContainsKey("VariantType") && f.Metadata["VariantType"].ToString() == "Head");
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Skip_Disabled_Options()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "SkipDisabled");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""enabled.patch_0""],
                    ""enabled"": true
                },
                {
                    ""id"": ""armor2"",
                    ""files"": [""disabled.patch_0""],
                    ""enabled"": false
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "enabled.patch_0"), "data");
        File.WriteAllText(Path.Combine(modPath, "disabled.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.Single(files);
        Assert.Contains(files, f => f.TargetRelativePath == "enabled.patch_0");
        Assert.DoesNotContain(files, f => f.TargetRelativePath == "disabled.patch_0");
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Set_Correct_Priority()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "Priority");
        Directory.CreateDirectory(modPath);

        var manifestJson = @"{
            ""gameplayArmors"": [
                {
                    ""id"": ""armor1"",
                    ""files"": [""test.patch_0""],
                    ""enabled"": true
                }
            ]
        }";

        File.WriteAllText(Path.Combine(modPath, "armor_manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(modPath, "test.patch_0"), "data");

        var mod = new Mod { Path = modPath };

        // Act
        var files = await _workflow.GetDeploymentFilesAsync(modPath);

        // Assert
        Assert.All(files, f => Assert.Equal(10, f.Priority));
    }
}
