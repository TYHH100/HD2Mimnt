using BenchmarkDotNet.Attributes;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using Moq;

namespace HD2Mimnt.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class SearchBenchmarks
{
    private HD2MimntDbContext? _dbContext;
    private ModRepository? _modRepository;
    private SearchService? _searchService;
    private string? _testDbPath;
    private List<Guid> _modIds = new();

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"benchmark_search_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        await _dbContext.InitializeDatabaseAsync();

        _modRepository = new ModRepository(_dbContext);

        // Seed 1000 mods with varied data
        for (int i = 0; i < 1000; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Test Mod {i}",
                Version = $"{i % 10}.{i % 100}.{i}",
                Author = $"Author{i % 50}",
                Description = $"Description for test mod {i}. Contains various keywords like armor, weapon, gameplay, balance.",
                Path = $"C:\\Mods\\Mod{i}",
                Category = (ModCategory)(i % 5),
                Tags = GenerateTags(i),
                WorkflowType = i % 2 == 0 ? "Legacy" : "V1",
                ImportedAt = DateTime.UtcNow.AddDays(-i),
                CreatedAt = DateTime.UtcNow.AddDays(-i),
                ModifiedAt = DateTime.UtcNow.AddDays(-i),
                Size = 1024 * 1024 * (i % 100 + 1),
                Hash = Guid.NewGuid().ToString(),
                Source = i % 3 == 0 ? ModSource.NexusMods : ModSource.LocalFolder,
                Status = ModStatus.Ready
            };

            await _modRepository.AddAsync(mod);
            _modIds.Add(mod.Id);
        }

        var mockLogger = new Mock<ILogger<SearchService>>();
        _searchService = new SearchService(_modRepository, mockLogger.Object);
    }

    private static List<string> GenerateTags(int index)
    {
        var tags = new List<string>();
        if (index % 2 == 0) tags.Add("armor");
        if (index % 3 == 0) tags.Add("weapon");
        if (index % 5 == 0) tags.Add("gameplay");
        if (index % 7 == 0) tags.Add("balance");
        if (index % 11 == 0) tags.Add("cosmetic");
        return tags;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbContext?.Dispose();
        if (!string.IsNullOrEmpty(_testDbPath) && File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    [Benchmark]
    public async Task SearchByName_SingleKeyword()
    {
        var results = await _searchService!.SearchAsync("Mod 50");
    }

    [Benchmark]
    public async Task SearchByName_PartialMatch()
    {
        var results = await _searchService!.SearchAsync("Test");
    }

    [Benchmark]
    public async Task SearchByAuthor()
    {
        var results = await _searchService!.SearchAsync("Author10");
    }

    [Benchmark]
    public async Task SearchByCategory()
    {
        var results = await _modRepository!.GetByCategoryAsync(ModCategory.Armor);
    }

    [Benchmark]
    public async Task SearchByTag()
    {
        var results = await _searchService!.SearchAsync("armor");
    }

    [Benchmark]
    public async Task SearchByMultipleTags()
    {
        var results = await _searchService!.SearchAsync("armor weapon");
    }

    [Benchmark]
    public async Task SearchComplex_NameAndCategory()
    {
        var results = await _searchService!.SearchAsync("Mod");
    }

    [Benchmark]
    public async Task EmptySearch_ReturnAll()
    {
        var results = await _modRepository!.GetAllAsync();
    }

    [Benchmark]
    public async Task SearchNoResults()
    {
        var results = await _searchService!.SearchAsync("NonExistentMod12345");
    }

    [Benchmark]
    public async Task SearchWithPagination()
    {
        var results = await _searchService!.SearchAsync("Mod");
    }
}
