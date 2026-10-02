using BenchmarkDotNet.Attributes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class SQLiteBenchmarks
{
    private HD2MimntDbContext? _dbContext;
    private ModRepository? _modRepository;
    private ProfileRepository? _profileRepository;
    private string? _testDbPath;
    private List<Guid> _modIds = new();
    private Guid _profileId;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"benchmark_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        await _dbContext.InitializeDatabaseAsync();

        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);

        // Seed 1000 mods for testing
        for (int i = 0; i < 1000; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Test Mod {i}",
                Version = "1.0.0",
                Author = $"Author {i % 10}",
                Description = $"Description for mod {i}",
                Path = $"C:\\Mods\\Mod{i}",
                Category = (ModCategory)(i % 5),
                WorkflowType = i % 2 == 0 ? "Legacy" : "V1",
                ImportedAt = DateTime.UtcNow.AddDays(-i),
                CreatedAt = DateTime.UtcNow.AddDays(-i),
                ModifiedAt = DateTime.UtcNow.AddDays(-i),
                Size = 1024 * 1024 * (i % 100),
                Hash = Guid.NewGuid().ToString(),
                Source = ModSource.LocalFolder,
                Status = ModStatus.Ready
            };

            await _modRepository.AddAsync(mod);
            _modIds.Add(mod.Id);
        }

        // Create a test profile
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            Description = "Benchmark test profile",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        // Add 100 mods to profile
        for (int i = 0; i < 100; i++)
        {
            profile.Mods.Add(new ProfileMod
            {
                ProfileId = profile.Id,
                ModId = _modIds[i],
                Order = i,
                Enabled = true
            });
        }

        await _profileRepository.AddAsync(profile);
        _profileId = profile.Id;
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
    public async Task QueryById()
    {
        var mod = await _modRepository!.GetByIdAsync(_modIds[500]);
    }

    [Benchmark]
    public async Task QueryAll_1000Records()
    {
        var mods = await _modRepository!.GetAllAsync();
    }

    [Benchmark]
    public async Task QueryByCategory()
    {
        var mods = await _modRepository!.GetByCategoryAsync(ModCategory.Armor);
    }

    [Benchmark]
    public async Task InsertSingleMod()
    {
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "New Benchmark Mod",
            Version = "1.0.0",
            Author = "Benchmark Author",
            Description = "Benchmark test mod",
            Path = "C:\\Mods\\BenchmarkMod",
            Category = ModCategory.Weapon,
            WorkflowType = "Legacy",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Size = 1024 * 1024,
            Hash = Guid.NewGuid().ToString(),
            Source = ModSource.LocalFolder,
            Status = ModStatus.Ready
        };

        await _modRepository!.AddAsync(mod);
    }

    [Benchmark]
    public async Task UpdateModMetadata()
    {
        var mod = await _modRepository!.GetByIdAsync(_modIds[500]);
        if (mod != null)
        {
            mod.Name = "Updated Name";
            mod.Description = "Updated Description";
            await _modRepository.UpdateAsync(mod);
        }
    }

    [Benchmark]
    public async Task ComplexJoinQuery_ProfileWithMods()
    {
        var profile = await _profileRepository!.GetByIdAsync(_profileId);
        // Profile.Mods is loaded automatically by GetByIdAsync
    }

    [Benchmark]
    public async Task SearchByName()
    {
        var mods = await _modRepository!.SearchAsync("Mod 5");
    }
}
