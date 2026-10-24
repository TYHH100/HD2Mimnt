using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests;

public class ModServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly ModService _modService;
    private readonly string _testDbPath;
    private readonly string _testStoragePath;

    public ModServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_{Guid.NewGuid()}.db");
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"test_mods_{Guid.NewGuid()}");

        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _modRepository = new ModRepository(_dbContext);
        _modService = new ModService(_modRepository, _testStoragePath);
    }

    [Fact]
    public async Task GetAllMods_ShouldReturnAllMods()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Version = "1.0.0", Path = @"C:\Test\Mod1" };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Version = "2.0.0", Path = @"C:\Test\Mod2" };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        // Act
        var mods = await _modService.GetAllModsAsync();

        // Assert
        Assert.Equal(2, mods.Count());
    }

    [Fact]
    public async Task GetModById_ShouldReturnCorrectMod()
    {
        // Arrange
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Test Mod", Version = "1.0.0", Path = @"C:\Test\Mod" };
        await _modRepository.AddAsync(mod);

        // Act
        var retrievedMod = await _modService.GetModByIdAsync(mod.Id);

        // Assert
        Assert.NotNull(retrievedMod);
        Assert.Equal(mod.Name, retrievedMod.Name);
        Assert.Equal(mod.Version, retrievedMod.Version);
    }

    [Theory]
    [InlineData("", "preview.png", true)]
    [InlineData("missing.png", "preview.png", true)]
    [InlineData("custom.png", "preview.png", false)]
    [InlineData("", "missing.png", false)]
    [InlineData("", "../outside.png", false)]
    public async Task GetAllMods_ManifestIcon_RepairsMissingPreviewAndPreservesCustomImage(
        string thumbnail, string icon, bool repaired)
    {
        var path = Path.Combine(_testStoragePath, "mod");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "preview.png"), "preview");
        await File.WriteAllTextAsync(Path.Combine(path, "custom.png"), "custom");
        await File.WriteAllTextAsync(Path.Combine(_testStoragePath, "outside.png"), "outside");
        var original = thumbnail.Length == 0 ? "" : Path.Combine(path, thumbnail);
        var mod = new Mod
        {
            Id = Guid.NewGuid(), Name = "Migrated", Path = path, ResourceTypeIds = [],
            Manifest = new() { IconPath = icon }, ThumbnailPath = original
        };
        await _modRepository.AddAsync(mod);

        var loaded = Assert.Single(await _modService.GetAllModsAsync());

        var expected = repaired ? Path.Combine(path, "preview.png") : original;
        Assert.Equal(expected, loaded.ThumbnailPath);
        Assert.Equal(expected, (await _modRepository.GetByIdAsync(mod.Id))!.ThumbnailPath);
    }

    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData("", true, true, true)]
    [InlineData("missing.png", true, true, true)]
    [InlineData("", false, true, false)]
    [InlineData("missing.png", false, true, false)]
    [InlineData("", false, false, false)]
    public async Task ImportMod_WithoutDeclaredPreview_UsesOnlyRootImage(
        string? icon, bool rootImage, bool nestedImage, bool expectedPreview)
    {
        var source = Path.Combine(_testStoragePath, "source");
        Directory.CreateDirectory(Path.Combine(source, "images"));
        if (icon != null)
            await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Name = "RootPreview", IconPath = icon }));
        await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "content");
        void SaveImage(string relative, System.Drawing.Color color)
        {
            using var bitmap = new System.Drawing.Bitmap(4, 4);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(color);
            bitmap.Save(Path.Combine(source, relative), System.Drawing.Imaging.ImageFormat.Png);
        }
        if (rootImage) SaveImage("封面.PNG", System.Drawing.Color.Blue);
        if (nestedImage) SaveImage("images/preview.png", System.Drawing.Color.Red);

        var id = await _modService.ImportModAsync(source);
        var mod = (await _modRepository.GetByIdAsync(id))!;

        if (expectedPreview)
        {
            Assert.True(File.Exists(mod.ThumbnailPath));
            using var thumbnail = new System.Drawing.Bitmap(mod.ThumbnailPath);
            Assert.Equal(System.Drawing.Color.Blue.ToArgb(), thumbnail.GetPixel(0, 0).ToArgb());
        }
        else
        {
            Assert.Empty(mod.ThumbnailPath);
            Assert.Null(_modService.GetThumbnailPath(id));
        }
    }

    [Fact]
    public async Task ImportMod_ManifestIcon_TakesPriorityOverOtherPreviewImages()
    {
        var source = Path.Combine(_testStoragePath, "source");
        Directory.CreateDirectory(Path.Combine(source, "images"));
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            """{"Version":1,"Name":"ChosenIcon","IconPath":"images/封面.png"}""");
        foreach (var (relative, color) in new[]
        {
            ("preview.png", System.Drawing.Color.Blue), ("images/封面.png", System.Drawing.Color.Red)
        })
        {
            using var bitmap = new System.Drawing.Bitmap(4, 4);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(color);
            bitmap.Save(Path.Combine(source, relative), System.Drawing.Imaging.ImageFormat.Png);
        }

        var id = await _modService.ImportModAsync(source);

        var mod = (await _modRepository.GetByIdAsync(id))!;
        Assert.True(File.Exists(mod.ThumbnailPath));
        using var thumbnail = new System.Drawing.Bitmap(mod.ThumbnailPath);
        Assert.Equal(System.Drawing.Color.Red.ToArgb(), thumbnail.GetPixel(128, 128).ToArgb());
    }

    [Fact]
    public async Task GetModsByIds_ShouldReturnOnlyRequestedMods()
    {
        var requested = new Mod { Id = Guid.NewGuid(), Name = "Requested", Version = "1.0.0", Path = @"C:\Test\Requested" };
        var other = new Mod { Id = Guid.NewGuid(), Name = "Other", Version = "1.0.0", Path = @"C:\Test\Other" };
        await _modRepository.AddAsync(requested);
        await _modRepository.AddAsync(other);

        var result = (await _modService.GetModsByIdsAsync(new[] { requested.Id, Guid.NewGuid() })).ToList();

        var mod = Assert.Single(result);
        Assert.Equal(requested.Id, mod.Id);
    }

    [Fact]
    public async Task SearchMods_ShouldReturnMatchingMods()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Weapon Mod", Version = "1.0.0", Path = @"C:\Test\Mod1" };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Armor Mod", Version = "1.0.0", Path = @"C:\Test\Mod2" };
        var mod3 = new Mod { Id = Guid.NewGuid(), Name = "UI Enhancement", Version = "1.0.0", Path = @"C:\Test\Mod3" };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);
        await _modRepository.AddAsync(mod3);

        // Act
        var results = await _modService.SearchModsAsync("Mod");

        // Assert
        Assert.Equal(2, results.Count());
    }

    [Fact]
    public async Task GetModsByCategory_ShouldReturnFilteredMods()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Weapon Mod", Version = "1.0.0", Path = @"C:\Test\Mod1", Category = ModCategory.Gameplay };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "UI Mod", Version = "1.0.0", Path = @"C:\Test\Mod2", Category = ModCategory.UI };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        // Act
        var gameplayMods = await _modService.GetModsByCategoryAsync(ModCategory.Gameplay);

        // Assert
        Assert.Single(gameplayMods);
        Assert.Equal("Weapon Mod", gameplayMods.First().Name);
    }

    [Fact]
    public async Task AddTag_ShouldAddTagToMod()
    {
        // Arrange
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Test Mod", Version = "1.0.0", Path = @"C:\Test\Mod", Tags = new List<string>() };
        await _modRepository.AddAsync(mod);

        // Act
        await _modService.AddTagAsync(mod.Id, "weapon");

        // Assert
        var updatedMod = await _modService.GetModByIdAsync(mod.Id);
        Assert.NotNull(updatedMod);
        Assert.Contains("weapon", updatedMod.Tags);
    }

    [Fact]
    public async Task RemoveTag_ShouldRemoveTagFromMod()
    {
        // Arrange
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Test Mod", Version = "1.0.0", Path = @"C:\Test\Mod", Tags = new List<string> { "weapon", "balance" } };
        await _modRepository.AddAsync(mod);

        // Act
        await _modService.RemoveTagAsync(mod.Id, "weapon");

        // Assert
        var updatedMod = await _modService.GetModByIdAsync(mod.Id);
        Assert.NotNull(updatedMod);
        Assert.DoesNotContain("weapon", updatedMod.Tags);
        Assert.Contains("balance", updatedMod.Tags);
    }

    [Fact]
    public async Task UpdateModMetadata_ShouldUpdateFields()
    {
        // Arrange
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Original Name", Version = "1.0.0", Path = @"C:\Test\Mod", Description = "Original Description" };
        await _modRepository.AddAsync(mod);

        // Act
        await _modService.UpdateModMetadataAsync(mod.Id, name: "New Name", description: "New Description");

        // Assert
        var updatedMod = await _modService.GetModByIdAsync(mod.Id);
        Assert.NotNull(updatedMod);
        Assert.Equal("New Name", updatedMod.Name);
        Assert.Equal("New Description", updatedMod.Description);
    }

    [Fact]
    public async Task GetStatistics_ShouldReturnCorrectStats()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Version = "1.0.0", Path = @"C:\Test\Mod1", Size = 1024, Category = ModCategory.Gameplay, ContainsScripts = true, Tags = new List<string> { "weapon" } };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Version = "1.0.0", Path = @"C:\Test\Mod2", Size = 2048, Category = ModCategory.UI, ContainsScripts = false, Tags = new List<string> { "ui", "hud" } };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        // Act
        var stats = await _modService.GetStatisticsAsync();

        // Assert
        Assert.Equal(2, stats.TotalCount);
        Assert.Equal(3072, stats.TotalSize);
        Assert.Equal(1, stats.ScriptModCount);
        Assert.Equal(3, stats.TagCount);
    }

    [Fact]
    public async Task GetAllTags_ShouldReturnDistinctTags()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Version = "1.0.0", Path = @"C:\Test\Mod1", Tags = new List<string> { "weapon", "balance" } };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Version = "1.0.0", Path = @"C:\Test\Mod2", Tags = new List<string> { "ui", "weapon" } };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        // Act
        var tags = await _modService.GetAllTagsAsync();

        // Assert
        Assert.Equal(3, tags.Count());
        Assert.Contains("weapon", tags);
        Assert.Contains("balance", tags);
        Assert.Contains("ui", tags);
    }

    public void Dispose()
    {
        _dbContext.Dispose();

        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }

        if (Directory.Exists(_testStoragePath))
        {
            Directory.Delete(_testStoragePath, true);
        }
    }
}
