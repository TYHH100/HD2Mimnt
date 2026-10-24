using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Repositories;

public class LogicalModRepositoryTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly LogicalModRepository _repository;
    private readonly string _testDbPath;

    public LogicalModRepositoryTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_logicalmod_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _repository = new LogicalModRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_PersistsFullAggregate()
    {
        var mod = CreateMod("Alpha Mod");

        await _repository.AddAsync(mod);

        var retrieved = await _repository.GetByIdAsync(mod.Id);
        Assert.NotNull(retrieved);
        Assert.Single(retrieved.Components);
        Assert.Single(retrieved.Components[0].DeployableFiles);
        Assert.Single(retrieved.Packages);
        Assert.Equal("Alpha Mod", retrieved.Name);
        Assert.Equal(mod.Components[0].ComponentId, retrieved.Components[0].ComponentId);
        Assert.Equal(mod.Packages[0].PhysicalPath, retrieved.Packages[0].PhysicalPath);
    }

    [Fact]
    public async Task GetAllAsync_LoadsAggregatesForAllMods()
    {
        var mod1 = CreateMod("Alpha Mod");
        var mod2 = CreateMod("Beta Mod");
        await _repository.AddAsync(mod1);
        await _repository.AddAsync(mod2);

        var all = await _repository.GetAllAsync();

        Assert.Equal(2, all.Count);
        var alpha = all.Single(m => m.Name == "Alpha Mod");
        var beta = all.Single(m => m.Name == "Beta Mod");
        Assert.Single(alpha.Components);
        Assert.Single(alpha.Packages);
        Assert.Single(alpha.Components[0].DeployableFiles);
        Assert.Single(beta.Components);
        Assert.Equal(mod2.Components[0].ComponentId, beta.Components[0].ComponentId);
    }

    [Fact]
    public async Task SearchByNameAsync_LoadsAggregates()
    {
        await _repository.AddAsync(CreateMod("Alpha Soundpack"));
        await _repository.AddAsync(CreateMod("Beta UiTweak"));

        var results = await _repository.SearchByNameAsync("Soundpack");

        var result = Assert.Single(results);
        Assert.Equal("Alpha Soundpack", result.Name);
        Assert.Single(result.Components);
        Assert.Single(result.Components[0].DeployableFiles);
        Assert.Single(result.Packages);
    }

    [Fact]
    public async Task DeleteAsync_RemovesModAndAllChildRows()
    {
        var mod = CreateMod("Doomed Mod");
        await _repository.AddAsync(mod);

        await _repository.DeleteAsync(mod.Id);

        Assert.Empty(await _repository.GetAllAsync());
        Assert.Null(await _repository.GetByIdAsync(mod.Id));

        using var connection = await _dbContext.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                (SELECT COUNT(*) FROM LogicalMods) AS Mods,
                (SELECT COUNT(*) FROM LogicalModComponents) AS Components,
                (SELECT COUNT(*) FROM DeployableFiles) AS Files,
                (SELECT COUNT(*) FROM PackageSources) AS Packages";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((0L, 0L, 0L, 0L),
            (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
    }

    [Fact]
    public async Task AddAsync_RollbackKeepsDatabaseCleanWhenChildInsertFails()
    {
        // 先写入一个合法聚合，再用同名主键触发主行插入失败（事务回滚不应留下任何子行）
        var mod = CreateMod("Original Mod");
        await _repository.AddAsync(mod);

        var conflicting = CreateMod("Broken Mod");
        conflicting.Id = mod.Id;

        await Assert.ThrowsAsync<SqliteException>(() => _repository.AddAsync(conflicting));

        var all = await _repository.GetAllAsync();
        var survivor = Assert.Single(all);
        Assert.Equal("Original Mod", survivor.Name);
        Assert.Single(survivor.Components);
        Assert.Single(survivor.Packages);
    }

    private static LogicalMod CreateMod(string name)
    {
        var componentId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        return new LogicalMod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Author = "tester",
            Description = "test mod",
            TotalSize = 1024,
            Components =
            [
                new LogicalModComponent
                {
                    Id = componentId,
                    ComponentId = $"component::{name}",
                    Type = ComponentType.Model,
                    Name = $"{name} component",
                    SourcePackageId = packageId,
                    Size = 512,
                    Enabled = true,
                    DeployableFiles =
                    [
                        new DeployableFile
                        {
                            Id = Guid.NewGuid(),
                            SourcePath = @"Packages\source.pak",
                            TargetPath = @"data\source.pak",
                            Size = 512,
                            Hash = "abc123",
                            PackageId = packageId,
                            IsShared = false
                        }
                    ]
                }
            ],
            Packages =
            [
                new PackageSource
                {
                    Id = packageId,
                    Name = $"{name}.zip",
                    PhysicalPath = Path.Combine(Path.GetTempPath(), $"{name}.zip"),
                    ArchiveType = ArchiveType.Zip,
                    Size = 1024,
                    Hash = "deadbeef",
                    LastModified = DateTime.UtcNow,
                    Depth = 0
                }
            ]
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch
        {
            // 忽略清理错误
        }
    }
}
