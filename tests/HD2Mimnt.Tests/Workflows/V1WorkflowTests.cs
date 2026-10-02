using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Workflows;
using Xunit;

namespace HD2Mimnt.Tests.Workflows;

/// <summary>
/// V1Workflow 测试
/// </summary>
public class V1WorkflowTests : IDisposable
{
    private readonly string _testDir;

    public V1WorkflowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"v1_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Name_Should_Return_V1()
    {
        // Arrange
        var workflow = new V1Workflow();

        // Act & Assert
        Assert.Equal("V1", workflow.Name);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_True_For_V1_Manifest()
    {
        // Arrange
        var workflow = new V1Workflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0",
            manifest_version = 1,
            options = new[]
            {
                new { name = "Option 1", description = "Test", default_enabled = true }
            }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_True_For_V1_File_Extensions()
    {
        // Arrange
        var workflow = new V1Workflow();

        var patchFile = Path.Combine(_testDir, "test.patch_0");
        await File.WriteAllTextAsync(patchFile, "patch content");

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Detect_Gpu_Resources_Files()
    {
        // Arrange
        var workflow = new V1Workflow();

        var gpuFile = Path.Combine(_testDir, "test.gpu_resources");
        await File.WriteAllTextAsync(gpuFile, "gpu content");

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Detect_Stream_Files()
    {
        // Arrange
        var workflow = new V1Workflow();

        var streamFile = Path.Combine(_testDir, "test.stream");
        await File.WriteAllTextAsync(streamFile, "stream content");

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_False_For_Empty_Directory()
    {
        // Arrange
        var workflow = new V1Workflow();

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Parse_V1_Manifest()
    {
        // Arrange
        var workflow = new V1Workflow();
        var manifest = new
        {
            guid = Guid.NewGuid().ToString(),
            name = "Test V1 Mod",
            description = "Test Description",
            version = "2.0.0",
            author = "Test Author",
            manifest_version = 1,
            category = "Armor",
            tags = new[] { "v1", "armor" },
            options = new object[]
            {
                new
                {
                    name = "Enable Feature A",
                    description = "Feature A description",
                    default_enabled = true,
                    sub_options = new object[]
                    {
                        new { name = "Variant 1", description = "First variant" },
                        new { name = "Variant 2", description = "Second variant" }
                    }
                },
                new
                {
                    name = "Enable Feature B",
                    description = "Feature B description",
                    default_enabled = false
                }
            }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.ParseManifestAsync(_testDir);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test V1 Mod", result.Name);
        Assert.Equal("Test Description", result.Description);
        Assert.Equal("2.0.0", result.Version);
        Assert.Equal("Test Author", result.Author);
        Assert.Equal("Armor", result.Category);
        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(ManifestVersion.V1, result.ManifestVersion);
        Assert.NotNull(result.Options);
        Assert.Equal(2, result.Options.Count);

        // 验证第一个选项
        var option1 = result.Options[0];
        Assert.Equal("Enable Feature A", option1.Name);
        Assert.True(option1.DefaultEnabled);
        Assert.NotNull(option1.SubOptions);
        Assert.Equal(2, option1.SubOptions.Count);
        Assert.Equal("Variant 1", option1.SubOptions[0].Name);

        // 验证第二个选项
        var option2 = result.Options[1];
        Assert.Equal("Enable Feature B", option2.Name);
        Assert.False(option2.DefaultEnabled);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Return_Null_For_Invalid_Directory()
    {
        // Arrange
        var workflow = new V1Workflow();

        // Act
        var result = await workflow.ParseManifestAsync(string.Empty);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_Should_Pass_For_Valid_V1_Mod()
    {
        // Arrange
        var workflow = new V1Workflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0",
            manifest_version = 1
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        var patchFile = Path.Combine(_testDir, "test.patch_0");
        await File.WriteAllTextAsync(patchFile, "patch content");

        // Act
        var result = await workflow.ValidateAsync(_testDir);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, (int)result.Metadata["FileCount"]);
        Assert.Equal(1, (int)result.Metadata["V1FileCount"]);
    }

    [Fact]
    public async Task ValidateAsync_Should_Fail_For_Empty_Directory()
    {
        // Arrange
        var workflow = new V1Workflow();

        // Act
        var result = await workflow.ValidateAsync(_testDir);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Return_All_Files_With_Priorities()
    {
        // Arrange
        var workflow = new V1Workflow();

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        var normalFile = Path.Combine(_testDir, "normal.pak");
        await File.WriteAllTextAsync(normalFile, "content");

        var patchFile = Path.Combine(_testDir, "test.patch_0");
        await File.WriteAllTextAsync(patchFile, "patch");

        var gpuFile = Path.Combine(_testDir, "test.gpu_resources");
        await File.WriteAllTextAsync(gpuFile, "gpu");

        var streamFile = Path.Combine(_testDir, "test.stream");
        await File.WriteAllTextAsync(streamFile, "stream");

        // Act
        var result = await workflow.GetDeploymentFilesAsync(_testDir);

        // Assert
        Assert.Equal(4, result.Count);

        // 验证普通文件优先级为 0
        var normalDeployment = result.First(f => f.SourcePath == normalFile);
        Assert.Equal(0, normalDeployment.Priority);
        Assert.False((bool)normalDeployment.Metadata["IsV1File"]);

        // 验证 V1 文件优先级为 10
        var patchDeployment = result.First(f => f.SourcePath == patchFile);
        Assert.Equal(10, patchDeployment.Priority);
        Assert.True((bool)patchDeployment.Metadata["IsV1File"]);

        var gpuDeployment = result.First(f => f.SourcePath == gpuFile);
        Assert.Equal(10, gpuDeployment.Priority);
        Assert.True((bool)gpuDeployment.Metadata["IsV1File"]);

        var streamDeployment = result.First(f => f.SourcePath == streamFile);
        Assert.Equal(10, streamDeployment.Priority);
        Assert.True((bool)streamDeployment.Metadata["IsV1File"]);
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Exclude_Manifest_And_Readme()
    {
        // Arrange
        var workflow = new V1Workflow();

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        var readme = Path.Combine(_testDir, "README.md");
        await File.WriteAllTextAsync(readme, "readme");

        var file = Path.Combine(_testDir, "file.pak");
        await File.WriteAllTextAsync(file, "content");

        // Act
        var result = await workflow.GetDeploymentFilesAsync(_testDir);

        // Assert
        Assert.Single(result);
        Assert.Equal(file, result[0].SourcePath);
    }

    [Fact]
    public async Task GetCleanupFilesAsync_Should_Return_All_Deployed_Files()
    {
        // Arrange
        var workflow = new V1Workflow();

        var file1 = Path.Combine(_testDir, "file1.pak");
        await File.WriteAllTextAsync(file1, "content1");

        var patchFile = Path.Combine(_testDir, "test.patch_0");
        await File.WriteAllTextAsync(patchFile, "patch");

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        // Act
        var result = await workflow.GetCleanupFilesAsync(_testDir);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("file1.pak", result);
        Assert.Contains("test.patch_0", result);
        Assert.DoesNotContain("manifest.json", result);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Handle_Options_Without_SubOptions()
    {
        // Arrange
        var workflow = new V1Workflow();
        var manifest = new
        {
            name = "Test Mod",
            options = new[]
            {
                new { name = "Simple Option", description = "No sub-options", default_enabled = true }
            }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.ParseManifestAsync(_testDir);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Options);
        Assert.Single(result.Options);
        Assert.Equal("Simple Option", result.Options[0].Name);
        Assert.Null(result.Options[0].SubOptions);
    }

    [Fact]
    public async Task ValidateAsync_Should_Count_SubOptions()
    {
        // Arrange
        var workflow = new V1Workflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0",
            options = new object[]
            {
                new
                {
                    name = "Option 1",
                    sub_options = new object[]
                    {
                        new { name = "Sub 1" },
                        new { name = "Sub 2" }
                    }
                },
                new
                {
                    name = "Option 2",
                    sub_options = new object[]
                    {
                        new { name = "Sub 3" }
                    }
                }
            }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        var file = Path.Combine(_testDir, "file.pak");
        await File.WriteAllTextAsync(file, "content");

        // Act
        var result = await workflow.ValidateAsync(_testDir);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(2, (int)result.Metadata["OptionsCount"]);
        Assert.Equal(3, (int)result.Metadata["SubOptionsCount"]);
    }
}
