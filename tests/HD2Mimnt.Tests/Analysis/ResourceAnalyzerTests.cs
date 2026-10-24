using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Tests.Analysis;

public class ResourceAnalyzerTests : IDisposable
{
    private readonly IResourceAnalyzer _analyzer;
    private readonly string _testDirectory;

    public ResourceAnalyzerTests()
    {
        _analyzer = new ResourceAnalyzer();
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
    public async Task AnalyzeAsync_Should_Build_Resource_Tree()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "TestMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(modPath, "README.md"), "# Test");

        // Act
        var tree = await _analyzer.AnalyzeAsync(modPath);

        // Assert
        Assert.NotNull(tree);
        Assert.Equal("TestMod", tree.Name);
        Assert.Equal(ResourceNodeType.Directory, tree.Type);
        Assert.Equal(2, tree.Children.Count);
        Assert.Contains(tree.Children, c => c.Name == "manifest.json");
        Assert.Contains(tree.Children, c => c.Name == "README.md");
    }

    [Fact]
    public async Task AnalyzeAsync_Should_Build_Nested_Tree()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "NestedMod");
        Directory.CreateDirectory(modPath);
        var texturesDir = Path.Combine(modPath, "textures");
        Directory.CreateDirectory(texturesDir);
        File.WriteAllText(Path.Combine(modPath, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(texturesDir, "armor.png"), "fake image");

        // Act
        var tree = await _analyzer.AnalyzeAsync(modPath);

        // Assert
        Assert.NotNull(tree);
        Assert.Equal(2, tree.Children.Count);
        var texturesNode = tree.Children.FirstOrDefault(c => c.Name == "textures");
        Assert.NotNull(texturesNode);
        Assert.Equal(ResourceNodeType.Directory, texturesNode.Type);
        Assert.Single(texturesNode.Children);
        Assert.Equal("armor.png", texturesNode.Children[0].Name);
    }

    [Fact]
    public async Task AnalyzeAsync_Should_Throw_When_Path_Empty()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _analyzer.AnalyzeAsync(""));
    }

    [Fact]
    public async Task AnalyzeAsync_Should_Throw_When_Directory_Not_Found()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent");

        // Act & Assert
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _analyzer.AnalyzeAsync(nonExistentPath));
    }

    [Fact]
    public async Task GetMetadataAsync_Should_Return_Metadata()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.json");
        File.WriteAllText(filePath, "{\"key\": \"value\"}");

        // Act
        var metadata = await _analyzer.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(filePath, metadata.FilePath);
        Assert.Equal("JSON Configuration", metadata.FileType);
        Assert.True(metadata.Size > 0);
        Assert.NotEmpty(metadata.Hash);
        Assert.False(metadata.IsV1File);
    }

    [Fact]
    public async Task GetMetadataAsync_Should_Identify_V1_Files()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "armor.patch_0");
        File.WriteAllText(filePath, "fake patch data");

        // Act
        var metadata = await _analyzer.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("V1 Patch File", metadata.FileType);
        Assert.True(metadata.IsV1File);
    }

    [Fact]
    public async Task GetMetadataAsync_Should_Throw_When_File_Not_Found()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.txt");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => _analyzer.GetMetadataAsync(filePath));
    }

    [Fact]
    public async Task CalculateHashAsync_Should_Return_SHA256_Hash()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "hashtest.txt");
        File.WriteAllText(filePath, "test content");

        // Act
        var hash1 = await _analyzer.CalculateHashAsync(filePath);
        var hash2 = await _analyzer.CalculateHashAsync(filePath);

        // Assert
        Assert.NotEmpty(hash1);
        Assert.Equal(64, hash1.Length); // SHA256 hex string length
        Assert.Equal(hash1, hash2); // Same file = same hash
    }

    [Fact]
    public async Task CalculateHashAsync_Should_Throw_When_File_Not_Found()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.txt");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => _analyzer.CalculateHashAsync(filePath));
    }

    [Fact]
    public async Task DetectReferencesAsync_Should_Detect_File_Paths_In_JSON()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "manifest.json");
        var jsonContent = @"{
            ""texture"": ""textures/armor.png"",
            ""model"": ""models/character.mesh""
        }";
        File.WriteAllText(filePath, jsonContent);

        // Act
        var references = await _analyzer.DetectReferencesAsync(filePath);

        // Assert
        Assert.NotEmpty(references);
        Assert.Contains(references, r => r.TargetFile == "textures/armor.png");
        Assert.Contains(references, r => r.TargetFile == "models/character.mesh");
        Assert.All(references, r => Assert.Equal(ReferenceType.Direct, r.Type));
    }

    [Fact]
    public async Task DetectReferencesAsync_Should_Return_Empty_For_Non_JSON()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "some text content");

        // Act
        var references = await _analyzer.DetectReferencesAsync(filePath);

        // Assert
        Assert.Empty(references);
    }

    [Fact]
    public async Task DetectReferencesAsync_Should_Handle_Invalid_JSON()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "invalid.json");
        File.WriteAllText(filePath, "{ invalid json");

        // Act
        var references = await _analyzer.DetectReferencesAsync(filePath);

        // Assert
        Assert.Empty(references); // Should not throw, just return empty
    }

    [Fact]
    public async Task AnalyzeAsync_Should_Set_File_Extensions()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "ExtTest");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "file.json"), "{}");
        File.WriteAllText(Path.Combine(modPath, "file.patch_0"), "data");

        // Act
        var tree = await _analyzer.AnalyzeAsync(modPath);

        // Assert
        var jsonFile = tree.Children.FirstOrDefault(c => c.Name == "file.json");
        var patchFile = tree.Children.FirstOrDefault(c => c.Name == "file.patch_0");
        Assert.NotNull(jsonFile);
        Assert.NotNull(patchFile);
        Assert.Equal(".json", jsonFile.Extension);
        Assert.Equal(".patch_0", patchFile.Extension);
    }

    [Fact]
    public async Task AnalyzeAsync_Should_Set_Relative_Paths()
    {
        // Arrange
        var modPath = Path.Combine(_testDirectory, "RelPathTest");
        Directory.CreateDirectory(modPath);
        var subDir = Path.Combine(modPath, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "content");

        // Act
        var tree = await _analyzer.AnalyzeAsync(modPath);

        // Assert
        var subNode = tree.Children.FirstOrDefault(c => c.Name == "sub");
        Assert.NotNull(subNode);
        var fileNode = subNode.Children.FirstOrDefault(c => c.Name == "file.txt");
        Assert.NotNull(fileNode);
        Assert.Equal(Path.Combine("sub", "file.txt"), fileNode.RelativePath);
    }
}
