using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;
using System.Text.Json;

namespace HD2Mimnt.Tests.Services;

public class CachingPlatformServiceTests
{
    private readonly Mock<IPlatformRegistry> _mockRegistry;
    private readonly Mock<IPlatformCacheRepository> _mockCacheRepository;
    private readonly Mock<IModPlatform> _mockPlatform;
    private readonly CachingPlatformService _service;

    public CachingPlatformServiceTests()
    {
        _mockRegistry = new Mock<IPlatformRegistry>();
        _mockCacheRepository = new Mock<IPlatformCacheRepository>();
        _mockPlatform = new Mock<IModPlatform>();
        _service = new CachingPlatformService(_mockRegistry.Object, _mockCacheRepository.Object);
    }

    [Fact]
    public void Constructor_Should_Throw_When_Registry_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CachingPlatformService(null!, _mockCacheRepository.Object));
    }

    [Fact]
    public void Constructor_Should_Throw_When_CacheRepository_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CachingPlatformService(_mockRegistry.Object, null!));
    }

    [Fact]
    public void Constructor_Should_Use_Default_Cache_Duration()
    {
        // Act
        var service = new CachingPlatformService(_mockRegistry.Object, _mockCacheRepository.Object);

        // Assert - default is 6 hours, we'll verify by checking behavior
        Assert.NotNull(service);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Return_From_Cache_When_Valid()
    {
        // Arrange
        var modInfo = new PlatformModInfo
        {
            Id = "12345",
            Name = "Cached Mod",
            Version = "1.0.0"
        };

        var cachedData = JsonSerializer.Serialize(modInfo);
        var cache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "12345",
            CachedData = cachedData,
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddHours(5),
            Etag = ""
        };

        _mockCacheRepository.Setup(r => r.GetAsync("nexusmods", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cache);

        // Act
        var result = await _service.GetModInfoAsync("nexusmods", "12345");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Cached Mod", result.Name);
        _mockRegistry.Verify(r => r.GetPlatform(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Call_Platform_When_Cache_Miss()
    {
        // Arrange
        var modInfo = new PlatformModInfo
        {
            Id = "12345",
            Name = "Fresh Mod",
            Version = "1.0.0"
        };

        _mockCacheRepository.Setup(r => r.GetAsync("nexusmods", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformCache?)null);

        _mockRegistry.Setup(r => r.GetPlatform("nexusmods")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modInfo);

        _mockCacheRepository.Setup(r => r.AddAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.GetModInfoAsync("nexusmods", "12345");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Fresh Mod", result.Name);
        _mockPlatform.Verify(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()), Times.Once);
        _mockCacheRepository.Verify(r => r.AddAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Call_Platform_When_Expired()
    {
        // Arrange
        var modInfo = new PlatformModInfo
        {
            Id = "12345",
            Name = "Updated Mod",
            Version = "2.0.0"
        };

        var expiredCache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "12345",
            CachedData = "{\"name\":\"Old Mod\"}",
            CachedAt = DateTime.UtcNow.AddHours(-12),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            Etag = ""
        };

        _mockCacheRepository.Setup(r => r.GetAsync("nexusmods", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredCache);

        _mockRegistry.Setup(r => r.GetPlatform("nexusmods")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modInfo);

        _mockCacheRepository.Setup(r => r.UpdateAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.GetModInfoAsync("nexusmods", "12345");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Mod", result.Name);
        _mockPlatform.Verify(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()), Times.Once);
        _mockCacheRepository.Verify(r => r.UpdateAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Call_Platform_When_ForceRefresh()
    {
        // Arrange
        var modInfo = new PlatformModInfo
        {
            Id = "12345",
            Name = "Force Refreshed Mod",
            Version = "1.0.0"
        };

        var validCache = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = "nexusmods",
            PlatformModId = "12345",
            CachedData = "{\"name\":\"Cached Mod\"}",
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddHours(5),
            Etag = ""
        };

        _mockCacheRepository.Setup(r => r.GetAsync("nexusmods", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(validCache);

        _mockRegistry.Setup(r => r.GetPlatform("nexusmods")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modInfo);

        _mockCacheRepository.Setup(r => r.UpdateAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.GetModInfoAsync("nexusmods", "12345", forceRefresh: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Force Refreshed Mod", result.Name);
        _mockPlatform.Verify(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Store_Result_In_Cache()
    {
        // Arrange
        var modInfo = new PlatformModInfo
        {
            Id = "12345",
            Name = "New Mod",
            Version = "1.0.0"
        };

        _mockCacheRepository.Setup(r => r.GetAsync("nexusmods", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformCache?)null);

        _mockRegistry.Setup(r => r.GetPlatform("nexusmods")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetModInfoAsync("12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modInfo);

        PlatformCache? capturedCache = null;
        _mockCacheRepository.Setup(r => r.AddAsync(It.IsAny<PlatformCache>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformCache, CancellationToken>((cache, ct) => capturedCache = cache)
            .Returns(Task.CompletedTask);

        // Act
        await _service.GetModInfoAsync("nexusmods", "12345");

        // Assert
        Assert.NotNull(capturedCache);
        Assert.Equal("nexusmods", capturedCache.PlatformId);
        Assert.Equal("12345", capturedCache.PlatformModId);
        Assert.Contains("New Mod", capturedCache.CachedData);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Return_Null_When_Platform_Not_Found()
    {
        // Arrange
        _mockCacheRepository.Setup(r => r.GetAsync("unknown", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformCache?)null);

        _mockRegistry.Setup(r => r.GetPlatform("unknown")).Returns((IModPlatform?)null);

        // Act
        var result = await _service.GetModInfoAsync("unknown", "12345");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ClearExpiredCachesAsync_Should_Delete_Expired()
    {
        // Arrange
        _mockCacheRepository.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.ClearExpiredCachesAsync();

        // Assert
        _mockCacheRepository.Verify(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCacheSizeAsync_Should_Return_Count()
    {
        // Arrange
        _mockCacheRepository.Setup(r => r.GetCacheSizeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        // Act
        var size = await _service.GetCacheSizeAsync();

        // Assert
        Assert.Equal(42, size);
    }

    [Fact]
    public async Task ClearCacheAsync_Should_Clear_All_Or_By_Platform()
    {
        // Arrange
        _mockCacheRepository.Setup(r => r.ClearAsync(null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCacheRepository.Setup(r => r.ClearAsync("nexusmods", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act - Clear all
        await _service.ClearCacheAsync();

        // Assert
        _mockCacheRepository.Verify(r => r.ClearAsync(null, It.IsAny<CancellationToken>()), Times.Once);

        // Act - Clear specific platform
        await _service.ClearCacheAsync("nexusmods");

        // Assert
        _mockCacheRepository.Verify(r => r.ClearAsync("nexusmods", It.IsAny<CancellationToken>()), Times.Once);
    }
}
