using HD2Mimnt.Application.Search;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public class SearchServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly SearchService _searchService;
    private readonly string _testDbPath;

    public SearchServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_search_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _modRepository = new ModRepository(_dbContext);
        _searchService = new SearchService(_modRepository);
    }

    [Fact]
    public async Task BuildIndex_ShouldBuildSuccessfully()
    {
        // Arrange
        await AddTestMods();

        // Act
        await _searchService.BuildIndexAsync();

        // Assert
        var stats = _searchService.GetIndexStats();
        Assert.True(stats.IsBuilt);
        Assert.True(stats.TotalMods > 0);
    }

    [Fact]
    public async Task SearchAsync_ExactMatch_ShouldReturnExactResult()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        // Act
        var results = await _searchService.SearchAsync("Armor Mod Alpha");

        // Assert
        var resultList = results.ToList();
        Assert.NotEmpty(resultList);
        Assert.Equal("Armor Mod Alpha", resultList.First().Name);
    }

    [Fact]
    public async Task SearchAsync_PrefixMatch_ShouldReturnResults()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        // Act
        var results = await _searchService.SearchAsync("Armor");

        // Assert
        var resultList = results.ToList();
        Assert.NotEmpty(resultList);
        Assert.All(resultList, mod => Assert.Contains("armor", mod.Name.ToLower()));
    }

    [Fact]
    public async Task SearchAsync_FuzzyMatch_ShouldReturnResults()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        // Act
        var results = await _searchService.SearchAsync("Weap");

        // Assert
        var resultList = results.ToList();
        Assert.NotEmpty(resultList);
        Assert.Contains(resultList, m => m.Name.Contains("Weapon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_TagSearch_ShouldReturnTaggedMods()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        // Act
        var results = await _searchService.SearchAsync("tag:armor");

        // Assert
        var resultList = results.ToList();
        Assert.NotEmpty(resultList);
        Assert.All(resultList, mod => Assert.Contains("armor", mod.Tags));
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ShouldReturnAllMods()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        // Act
        var results = await _searchService.SearchAsync("");

        // Assert
        var stats = _searchService.GetIndexStats();
        Assert.Equal(stats.TotalMods, results.Count());
    }

    [Fact]
    public async Task UpdateModIndex_ShouldUpdateIndex()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        var newMod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "New Test Mod",
            Version = "1.0.0",
            Author = "Test",
            Description = "Test",
            Path = "/test/path",
            Category = ModCategory.Gameplay,
            Tags = new List<string> { "test" },
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };

        await _modRepository.AddAsync(newMod);

        // Act
        _searchService.UpdateModIndex(newMod);

        // Assert
        var results = await _searchService.SearchAsync("New Test Mod");
        Assert.NotEmpty(results);
        Assert.Contains(results, m => m.Id == newMod.Id);
    }

    [Fact]
    public async Task RemoveModFromIndex_ShouldRemoveFromIndex()
    {
        // Arrange
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        var mods = await _modRepository.GetAllAsync();
        var modToRemove = mods.First();

        // Act
        _searchService.RemoveModFromIndex(modToRemove.Id);

        // Assert
        var results = await _searchService.SearchAsync(modToRemove.Name);
        Assert.DoesNotContain(results, m => m.Id == modToRemove.Id);
    }

    [Fact]
    public async Task SearchAsync_Author_ShouldReturnMatchingMods()
    {
        await AddTestMods();
        await _searchService.BuildIndexAsync();

        var results = (await _searchService.SearchAsync("Author C")).ToList();

        Assert.Contains(results, mod => mod.Name == "Weapon Enhancement");
    }

    [Fact]
    public async Task SearchAsync_PinyinOption_MatchesChineseName()
    {
        await _modRepository.AddAsync(new Mod
        {
            Id = Guid.NewGuid(),
            Name = "装甲模组",
            Version = "1.0.0",
            Author = "测试作者",
            Description = "测试",
            Path = "/test/chinese",
            Category = ModCategory.Visual,
            Tags = new List<string>(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        });
        await _searchService.BuildIndexAsync();

        var withoutPinyin = await _searchService.SearchAsync("zhuangjia", SearchMatchOptions.Default);
        var withPinyin = await _searchService.SearchAsync("zhuangjia", new SearchMatchOptions(Fuzzy: false, Pinyin: true));

        Assert.DoesNotContain(withoutPinyin, mod => mod.Name == "装甲模组");
        Assert.Contains(withPinyin, mod => mod.Name == "装甲模组");
    }

    [Fact]
    public async Task SearchAsync_FuzzyOption_MatchesNonAdjacentCharacters()
    {
        await _modRepository.AddAsync(new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Helldivers Enhancement",
            Version = "1.0.0",
            Author = "Test",
            Description = "Test",
            Path = "/test/fuzzy",
            Category = ModCategory.Gameplay,
            Tags = new List<string>(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        });
        await _searchService.BuildIndexAsync();

        var withoutFuzzy = await _searchService.SearchAsync("hldv", SearchMatchOptions.Default);
        var withFuzzy = await _searchService.SearchAsync("hldv", new SearchMatchOptions(Fuzzy: true, Pinyin: false));

        Assert.DoesNotContain(withoutFuzzy, mod => mod.Name == "Helldivers Enhancement");
        Assert.Contains(withFuzzy, mod => mod.Name == "Helldivers Enhancement");
    }

    [Fact]
    public async Task SearchAsync_Performance_ShouldBeUnder100ms()
    {
        // Arrange - Add 1000 mods
        for (int i = 0; i < 1000; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Mod {i} - {(i % 10 == 0 ? "Armor" : "Weapon")} - Test",
                Version = "1.0.0",
                Author = "Test",
                Description = "Test",
                Path = $"/test/path{i}",
                Category = i % 2 == 0 ? ModCategory.Gameplay : ModCategory.Visual,
                Tags = new List<string> { i % 10 == 0 ? "armor" : "weapon", "test" },
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            };
            await _modRepository.AddAsync(mod);
        }

        await _searchService.BuildIndexAsync();

        // Act
        var startTime = DateTime.UtcNow;
        var results = await _searchService.SearchAsync("Armor");
        var duration = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotEmpty(results);
        Assert.True(duration.TotalMilliseconds < 100, $"Search took {duration.TotalMilliseconds}ms, expected < 100ms");
    }

    private async Task AddTestMods()
    {
        var mods = new[]
        {
            new Mod
            {
                Id = Guid.NewGuid(),
                Name = "Armor Mod Alpha",
                Version = "1.0.0",
                Author = "Author A",
                Description = "Armor modification",
                Path = "/test/armor1",
                Category = ModCategory.Gameplay,
                Tags = new List<string> { "armor", "gameplay" },
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            },
            new Mod
            {
                Id = Guid.NewGuid(),
                Name = "Armor Mod Beta",
                Version = "1.0.0",
                Author = "Author B",
                Description = "Another armor mod",
                Path = "/test/armor2",
                Category = ModCategory.Visual,
                Tags = new List<string> { "armor", "visual" },
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            },
            new Mod
            {
                Id = Guid.NewGuid(),
                Name = "Weapon Enhancement",
                Version = "1.0.0",
                Author = "Author C",
                Description = "Weapon mod",
                Path = "/test/weapon1",
                Category = ModCategory.Gameplay,
                Tags = new List<string> { "weapon", "gameplay" },
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            },
            new Mod
            {
                Id = Guid.NewGuid(),
                Name = "Character Skin Pack",
                Version = "1.0.0",
                Author = "Author D",
                Description = "Skins",
                Path = "/test/character1",
                Category = ModCategory.Visual,
                Tags = new List<string> { "character", "visual" },
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            }
        };

        foreach (var mod in mods)
        {
            await _modRepository.AddAsync(mod);
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
