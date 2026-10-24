using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class PluginModAccessTests
{
    private readonly Mock<IModRepository> _mockModRepo;
    private readonly Mock<IPluginService> _mockPluginService;
    private readonly PluginModAccess _modAccess;
    private const string TestPluginId = "test.plugin";

    public PluginModAccessTests()
    {
        _mockModRepo = new Mock<IModRepository>();
        _mockPluginService = new Mock<IPluginService>();
        _modAccess = new PluginModAccess(_mockModRepo.Object, _mockPluginService.Object, TestPluginId);
    }

    [Fact]
    public async Task GetModAsync_WithPermission_ReturnsMod()
    {
        // Arrange
        var modId = Guid.NewGuid();
        var mod = new Mod
        {
            Id = modId,
            Name = "Test Mod",
            Description = "Test Description",
            Version = "1.0.0",
            Author = "Test Author",
            Category = ModCategory.Armor,
            Tags = new List<string> { "tag1", "tag2" },
            Path = "/path/to/mod",
            Size = 1024,
            ImportedAt = DateTime.UtcNow
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByIdAsync(modId, default)).ReturnsAsync(mod);

        // Act
        var result = await _modAccess.GetModAsync(modId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(modId, result.Id);
        Assert.Equal("Test Mod", result.Name);
        Assert.Equal("Test Description", result.Description);
        Assert.Equal(2, result.Tags.Count);
        Assert.Contains("tag1", result.Tags);
        Assert.Contains("tag2", result.Tags);
    }

    [Fact]
    public async Task GetModAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var modId = Guid.NewGuid();
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _modAccess.GetModAsync(modId));
    }

    [Fact]
    public async Task GetModAsync_ModNotFound_ReturnsNull()
    {
        // Arrange
        var modId = Guid.NewGuid();
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByIdAsync(modId, default)).ReturnsAsync((Mod?)null);

        // Act
        var result = await _modAccess.GetModAsync(modId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllModsAsync_WithPermission_ReturnsAllMods()
    {
        // Arrange
        var mods = new List<Mod>
        {
            new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Tags = new List<string> { "tag1" } },
            new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Tags = new List<string> { "tag2", "tag3" } }
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(mods);

        // Act
        var result = await _modAccess.GetAllModsAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Mod 1", result[0].Name);
        Assert.Single(result[0].Tags);
        Assert.Equal(2, result[1].Tags.Count);
    }

    [Fact]
    public async Task GetAllModsAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _modAccess.GetAllModsAsync());
    }

    [Fact]
    public async Task GetModsByCategoryAsync_WithPermission_ReturnsFilteredMods()
    {
        // Arrange
        var category = ModCategory.Armor;
        var mods = new List<Mod>
        {
            new Mod { Id = Guid.NewGuid(), Name = "Armor Mod 1", Category = category },
            new Mod { Id = Guid.NewGuid(), Name = "Armor Mod 2", Category = category }
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByCategoryAsync(category, default)).ReturnsAsync(mods);

        // Act
        var result = await _modAccess.GetModsByCategoryAsync("Armor");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, mod => Assert.Equal("Armor", mod.Category));
    }

    [Fact]
    public async Task GetModsByCategoryAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _modAccess.GetModsByCategoryAsync("Armor"));
    }

    [Fact]
    public async Task GetModsByTagAsync_WithPermission_ReturnsFilteredMods()
    {
        // Arrange
        var tag = "anime";
        var mods = new List<Mod>
        {
            new Mod { Id = Guid.NewGuid(), Name = "Anime Mod 1", Tags = new List<string> { "anime", "armor" } },
            new Mod { Id = Guid.NewGuid(), Name = "Anime Mod 2", Tags = new List<string> { "anime", "weapon" } }
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByTagAsync(tag, default)).ReturnsAsync(mods);

        // Act
        var result = await _modAccess.GetModsByTagAsync(tag);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, mod => Assert.Contains("anime", mod.Tags));
    }

    [Fact]
    public async Task GetModsByTagAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _modAccess.GetModsByTagAsync("anime"));
    }

    [Fact]
    public async Task GetModAsync_EmptyTags_ReturnsEmptyTagList()
    {
        // Arrange
        var modId = Guid.NewGuid();
        var mod = new Mod
        {
            Id = modId,
            Name = "Test Mod",
            Tags = []  // No tags
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByIdAsync(modId, default)).ReturnsAsync(mod);

        // Act
        var result = await _modAccess.GetModAsync(modId);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task GetModAsync_TagsWithMultipleValues_ProperlySplit()
    {
        // Arrange
        var modId = Guid.NewGuid();
        var mod = new Mod
        {
            Id = modId,
            Name = "Test Mod",
            Tags = new List<string> { "tag1", "tag2", "tag3" }
        };

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "mods.read", default)).ReturnsAsync(true);
        _mockModRepo.Setup(r => r.GetByIdAsync(modId, default)).ReturnsAsync(mod);

        // Act
        var result = await _modAccess.GetModAsync(modId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Tags.Count);
        Assert.Contains("tag1", result.Tags);
        Assert.Contains("tag2", result.Tags);
        Assert.Contains("tag3", result.Tags);
    }
}
