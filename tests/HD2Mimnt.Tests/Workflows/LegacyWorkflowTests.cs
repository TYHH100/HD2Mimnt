using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Workflows;
using Xunit;

namespace HD2Mimnt.Tests.Workflows;

/// <summary>
/// LegacyWorkflow 测试
/// </summary>
public class LegacyWorkflowTests : IDisposable
{
    private readonly string _testDir;

    public LegacyWorkflowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"legacy_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Name_Should_Return_Legacy()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        // Act & Assert
        Assert.Equal("Legacy", workflow.Name);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_True_For_Legacy_Manifest()
    {
        // Arrange
        var workflow = new LegacyWorkflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0",
            manifest_version = 0,
            legacy_options = new[] { "Option 1", "Option 2" }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_True_For_Manifest_Without_Version()
    {
        // Arrange
        var workflow = new LegacyWorkflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0"
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_False_For_Empty_Directory()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        // Act
        var result = await workflow.CanHandleAsync(_testDir);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CanHandleAsync_Should_Return_False_For_Invalid_Path()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        // Act
        var result = await workflow.CanHandleAsync(string.Empty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Parse_Legacy_Manifest()
    {
        // Arrange
        var workflow = new LegacyWorkflow();
        var manifest = new
        {
            guid = Guid.NewGuid().ToString(),
            name = "Test Legacy Mod",
            description = "Test Description",
            version = "1.0.0",
            author = "Test Author",
            category = "Gameplay",
            tags = new[] { "armor", "weapon" },
            legacy_options = new[] { "Option A", "Option B", "Option C" }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.ParseManifestAsync(_testDir);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Legacy Mod", result.Name);
        Assert.Equal("Test Description", result.Description);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("Test Author", result.Author);
        Assert.Equal("Gameplay", result.Category);
        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(ManifestVersion.Legacy, result.ManifestVersion);
        Assert.NotNull(result.LegacyOptions);
        Assert.Equal(3, result.LegacyOptions.Count);
        Assert.Contains("Option A", result.LegacyOptions);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Return_Null_For_Invalid_Directory()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        // Act
        var result = await workflow.ParseManifestAsync(string.Empty);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_Should_Pass_For_Valid_Legacy_Mod()
    {
        // Arrange
        var workflow = new LegacyWorkflow();
        var manifest = new
        {
            name = "Test Mod",
            version = "1.0.0",
            legacy_options = new[] { "Option 1" }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        var testFile = Path.Combine(_testDir, "test.pak");
        await File.WriteAllTextAsync(testFile, "test content");

        // Act
        var result = await workflow.ValidateAsync(_testDir);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, (int)result.Metadata["FileCount"]); // manifest.json + test.pak
    }

    [Fact]
    public async Task ValidateAsync_Should_Fail_For_Empty_Directory()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        // Act
        var result = await workflow.ValidateAsync(_testDir);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Return_All_Files_Except_Manifest()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        var file1 = Path.Combine(_testDir, "file1.pak");
        await File.WriteAllTextAsync(file1, "content1");

        var file2 = Path.Combine(_testDir, "file2.dat");
        await File.WriteAllTextAsync(file2, "content2");

        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        var file3 = Path.Combine(subDir, "file3.pak");
        await File.WriteAllTextAsync(file3, "content3");

        // Act
        var result = await workflow.GetDeploymentFilesAsync(_testDir);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains(result, f => f.SourcePath == file1);
        Assert.Contains(result, f => f.SourcePath == file2);
        Assert.Contains(result, f => f.SourcePath == file3);
        Assert.DoesNotContain(result, f => f.SourcePath == manifestPath);

        // 验证相对路径
        var file1Deployment = result.First(f => f.SourcePath == file1);
        Assert.Equal("file1.pak", file1Deployment.TargetRelativePath);

        var file3Deployment = result.First(f => f.SourcePath == file3);
        Assert.Equal(Path.Combine("subdir", "file3.pak"), file3Deployment.TargetRelativePath);
    }

    [Fact]
    public async Task GetDeploymentFilesAsync_Should_Exclude_Readme_Files()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        var file1 = Path.Combine(_testDir, "file1.pak");
        await File.WriteAllTextAsync(file1, "content1");

        var readme = Path.Combine(_testDir, "README.md");
        await File.WriteAllTextAsync(readme, "readme content");

        // Act
        var result = await workflow.GetDeploymentFilesAsync(_testDir);

        // Assert
        Assert.Single(result);
        Assert.Contains(result, f => f.SourcePath == file1);
        Assert.DoesNotContain(result, f => f.SourcePath == readme);
    }

    [Fact]
    public async Task GetCleanupFilesAsync_Should_Return_All_Deployed_Files()
    {
        // Arrange
        var workflow = new LegacyWorkflow();

        var file1 = Path.Combine(_testDir, "file1.pak");
        await File.WriteAllTextAsync(file1, "content1");

        var file2 = Path.Combine(_testDir, "file2.dat");
        await File.WriteAllTextAsync(file2, "content2");

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");

        // Act
        var result = await workflow.GetCleanupFilesAsync(_testDir);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("file1.pak", result);
        Assert.Contains("file2.dat", result);
        Assert.DoesNotContain("manifest.json", result);
    }

    [Fact]
    public async Task ParseManifestAsync_Should_Handle_Nexus_Data()
    {
        // Arrange
        var workflow = new LegacyWorkflow();
        var manifest = new
        {
            name = "Test Mod",
            nexus = new
            {
                mod_id = 12345,
                version = "2.0.0"
            }
        };

        var manifestPath = Path.Combine(_testDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // Act
        var result = await workflow.ParseManifestAsync(_testDir);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.NexusData);
        Assert.Equal(12345, result.NexusData.ModId);
        Assert.Equal("2.0.0", result.NexusData.Version);
    }
}
