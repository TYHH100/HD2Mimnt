using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using Moq;

namespace HD2Mimnt.Tests.Analysis;

public class ResourceExplorerServiceTests : IDisposable
{
    private readonly Mock<IModRepository> _mockModRepository;
    private readonly IResourceAnalyzer _resourceAnalyzer;
    private readonly ResourceExplorerService _service;
    private readonly string _testDirectory;

    public ResourceExplorerServiceTests()
    {
        _mockModRepository = new Mock<IModRepository>();
        _resourceAnalyzer = new ResourceAnalyzer();
        _service = new ResourceExplorerService(_resourceAnalyzer, _mockModRepository.Object);
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
    public async Task GetResourceTreeAsync_Should_Return_Tree()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "TestMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "manifest.json"), "{}");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var tree = await _service.GetResourceTreeAsync(modId);

        // Assert
        Assert.NotNull(tree);
        Assert.Equal("TestMod", tree.Name);
        Assert.Single(tree.Children);
    }

    [Fact]
    public async Task GetResourceTreeAsync_Should_Cache_Tree()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "CacheMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "file.txt"), "content");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var tree1 = await _service.GetResourceTreeAsync(modId);
        var tree2 = await _service.GetResourceTreeAsync(modId);

        // Assert
        Assert.Same(tree1, tree2); // Should return cached instance
        _mockModRepository.Verify(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetResourceTreeAsync_Should_Refresh_When_Forced()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "RefreshMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "file.txt"), "content");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var tree1 = await _service.GetResourceTreeAsync(modId);
        var tree2 = await _service.GetResourceTreeAsync(modId, forceRefresh: true);

        // Assert
        Assert.NotSame(tree1, tree2); // Should not be cached instance
        _mockModRepository.Verify(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetResourceTreeAsync_Should_Throw_When_Mod_Not_Found()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Mod?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetResourceTreeAsync(modId));
    }

    [Fact]
    public async Task SearchResourcesAsync_Should_Find_Matching_Files()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "SearchMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "armor.json"), "{}");
        File.WriteAllText(Path.Combine(modPath, "weapon.json"), "");
        File.WriteAllText(Path.Combine(modPath, "armor.patch_0"), "data");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var results = await _service.SearchResourcesAsync(modId, "armor");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "armor.json");
        Assert.Contains(results, r => r.Name == "armor.patch_0");
    }

    [Fact]
    public async Task SearchResourcesAsync_Should_Return_Empty_For_No_Matches()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "NoMatchMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "file.txt"), "content");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var results = await _service.SearchResourcesAsync(modId, "nonexistent");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDependenciesAsync_Should_Detect_JSON_References()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "DepMod");
        Directory.CreateDirectory(modPath);
        var manifestPath = Path.Combine(modPath, "manifest.json");
        File.WriteAllText(manifestPath, @"{""texture"": ""textures/armor.png""}");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var dependency = await _service.GetDependenciesAsync(modId, "manifest.json");

        // Assert
        Assert.NotNull(dependency);
        Assert.Equal(modId, dependency.ModId);
        Assert.Equal("manifest.json", dependency.ResourcePath);
        Assert.Contains("textures/armor.png", dependency.DependsOn);
    }

    [Fact]
    public async Task GetDependenciesAsync_Should_Detect_V1_File_Pairing()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "V1DepMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "armor.patch_0"), "patch data");
        File.WriteAllText(Path.Combine(modPath, "armor.stream"), "stream data");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var dependency = await _service.GetDependenciesAsync(modId, "armor.patch_0");

        // Assert
        Assert.NotNull(dependency);
        Assert.Contains("armor.stream", dependency.DependsOn);
    }

    [Fact]
    public async Task ValidateIntegrityAsync_Should_Detect_Missing_References()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "ValidateMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "manifest.json"), @"{""texture"": ""missing.png""}");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var errors = await _service.ValidateIntegrityAsync(modId);

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("missing.png"));
    }

    [Fact]
    public async Task ValidateIntegrityAsync_Should_Detect_Missing_V1_Pairs()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "V1ValidateMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "armor.patch_0"), "patch data");
        // Missing armor.stream

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var errors = await _service.ValidateIntegrityAsync(modId);

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Missing paired .stream file"));
    }

    [Fact]
    public async Task ValidateIntegrityAsync_Should_Return_Empty_For_Valid_Mod()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        var modPath = Path.Combine(_testDirectory, "ValidMod");
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(modPath, "armor.patch_0"), "patch");
        File.WriteAllText(Path.Combine(modPath, "armor.stream"), "stream");

        var mod = new Mod { Id = Guid.Parse(modId), Path = modPath };
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var errors = await _service.ValidateIntegrityAsync(modId);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ValidateIntegrityAsync_Should_Return_Error_When_Mod_Not_Found()
    {
        // Arrange
        var modId = Guid.NewGuid().ToString();
        _mockModRepository.Setup(r => r.GetByIdAsync(Guid.Parse(modId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Mod?)null);

        // Act
        var errors = await _service.ValidateIntegrityAsync(modId);

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Mod not found"));
    }
}
