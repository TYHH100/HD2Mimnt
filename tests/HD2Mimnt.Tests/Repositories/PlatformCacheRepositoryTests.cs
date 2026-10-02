using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Repositories;

public class PlatformCacheRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly HD2MimntDbContext _context;
    private readonly PlatformCacheRepository _repository;

    public PlatformCacheRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_cache_{Guid.NewGuid()}.db");
        _context = new HD2MimntDbContext(_dbPath);
        _context.InitializeDatabaseAsync().Wait();
        _repository = new PlatformCacheRepository(_context);
    }

    [Fact]
    public void Constructor_Should_Throw_When_Context_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new PlatformCacheRepository(null!));
    }

    [Fact]
    public async Task GetAsync_Should_Return_Null_When_Not_Found()
    {
        // Act
        var result = await _repository.GetAsync("nexusmods", "12345");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_Should_Return_Cache_When_Found()
    {
        // Arrange
        var cache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "12345",
            CachedData = "{\"name\":\"Test Mod\"}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = "abc123"
        };
        await _repository.AddAsync(cache);

        // Act
        var result = await _repository.GetAsync("nexusmods", "12345");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(cache.Id, result.Id);
        Assert.Equal("nexusmods", result.PlatformId);
        Assert.Equal("12345", result.PlatformModId);
    }

    [Fact]
    public async Task AddAsync_Should_Insert_Cache()
    {
        // Arrange
        var cache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "github",
            PlatformModId = "owner/repo",
            CachedData = "{\"name\":\"GitHub Mod\"}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = "def456"
        };

        // Act
        await _repository.AddAsync(cache);

        // Assert
        var result = await _repository.GetAsync("github", "owner/repo");
        Assert.NotNull(result);
        Assert.Equal(cache.Id, result.Id);
    }

    [Fact]
    public async Task UpdateAsync_Should_Modify_Cache()
    {
        // Arrange
        var cache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "99999",
            CachedData = "{\"name\":\"Old Data\"}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = "old"
        };
        await _repository.AddAsync(cache);

        // Act
        cache.CachedData = "{\"name\":\"New Data\"}";
        cache.Etag = "new";
        await _repository.UpdateAsync(cache);

        // Assert
        var result = await _repository.GetAsync("nexusmods", "99999");
        Assert.NotNull(result);
        Assert.Equal("{\"name\":\"New Data\"}", result.CachedData);
        Assert.Equal("new", result.Etag);
    }

    [Fact]
    public async Task DeleteAsync_Should_Remove_Cache()
    {
        // Arrange
        var cache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "11111",
            CachedData = "{\"name\":\"To Delete\"}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = "delete"
        };
        await _repository.AddAsync(cache);

        // Act
        await _repository.DeleteAsync(cache.Id);

        // Assert
        var result = await _repository.GetAsync("nexusmods", "11111");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetExpiredAsync_Should_Return_Only_Expired()
    {
        // Arrange
        var expired = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "expired",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow.AddHours(-12),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            Etag = ""
        };

        var valid = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "valid",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        };

        await _repository.AddAsync(expired);
        await _repository.AddAsync(valid);

        // Act
        var result = await _repository.GetExpiredAsync(DateTime.UtcNow);

        // Assert
        Assert.Single(result);
        Assert.Equal(expired.Id, result[0].Id);
    }

    [Fact]
    public async Task DeleteExpiredAsync_Should_Remove_Only_Expired()
    {
        // Arrange
        var expired = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "expired2",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow.AddHours(-12),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            Etag = ""
        };

        var valid = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "valid2",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        };

        await _repository.AddAsync(expired);
        await _repository.AddAsync(valid);

        // Act
        await _repository.DeleteExpiredAsync(DateTime.UtcNow);

        // Assert
        var expiredResult = await _repository.GetAsync("nexusmods", "expired2");
        var validResult = await _repository.GetAsync("nexusmods", "valid2");
        Assert.Null(expiredResult);
        Assert.NotNull(validResult);
    }

    [Fact]
    public async Task GetCacheSizeAsync_Should_Return_Count()
    {
        // Arrange
        await _repository.AddAsync(new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "1",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        });

        await _repository.AddAsync(new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "github",
            PlatformModId = "2",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        });

        // Act
        var size = await _repository.GetCacheSizeAsync();

        // Assert
        Assert.Equal(2, size);
    }

    [Fact]
    public async Task ClearAsync_Should_Remove_All_Or_By_Platform()
    {
        // Arrange
        await _repository.AddAsync(new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "1",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        });

        await _repository.AddAsync(new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "github",
            PlatformModId = "2",
            CachedData = "{}",
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(6),
            Etag = ""
        });

        // Act - Clear only nexusmods
        await _repository.ClearAsync("nexusmods");

        // Assert
        var nexus = await _repository.GetAsync("nexusmods", "1");
        var github = await _repository.GetAsync("github", "2");
        Assert.Null(nexus);
        Assert.NotNull(github);

        // Act - Clear all
        await _repository.ClearAsync();

        // Assert
        var size = await _repository.GetCacheSizeAsync();
        Assert.Equal(0, size);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
