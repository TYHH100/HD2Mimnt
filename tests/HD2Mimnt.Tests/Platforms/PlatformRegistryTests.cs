using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;
using Moq;

namespace HD2Mimnt.Tests.Platforms;

public class PlatformRegistryTests : IDisposable
{
    private readonly PlatformRegistry _registry;
    private readonly HttpClient _httpClient;

    public PlatformRegistryTests()
    {
        _registry = new PlatformRegistry();
        _httpClient = new HttpClient();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public void RegisterPlatform_Should_Add_Platform()
    {
        // Arrange
        var platform = new MockPlatformAdapter(_httpClient);

        // Act
        _registry.RegisterPlatform(platform);

        // Assert
        var retrieved = _registry.GetPlatform(platform.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(platform.Id, retrieved.Id);
    }

    [Fact]
    public void GetPlatform_Should_Return_Registered_Platform()
    {
        // Arrange
        var platform = new MockPlatformAdapter(_httpClient);
        _registry.RegisterPlatform(platform);

        // Act
        var retrieved = _registry.GetPlatform("mock");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("mock", retrieved.Id);
        Assert.Equal("Mock Platform", retrieved.Name);
    }

    [Fact]
    public void GetPlatform_Should_Return_Null_For_Unknown()
    {
        // Act
        var retrieved = _registry.GetPlatform("unknown");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public void GetAllPlatforms_Should_Return_All()
    {
        // Arrange
        var platform1 = new MockPlatformAdapter(_httpClient);
        _registry.RegisterPlatform(platform1);

        // Act
        var platforms = _registry.GetAllPlatforms();

        // Assert
        Assert.Single(platforms);
        Assert.Contains(platforms, p => p.Id == "mock");
    }

    [Fact]
    public async Task GetAvailablePlatforms_Should_Only_Return_Available()
    {
        // Arrange
        var availablePlatform = new MockPlatformAdapter(_httpClient);
        _registry.RegisterPlatform(availablePlatform);

        var mockUnavailable = new Mock<IModPlatform>();
        mockUnavailable.Setup(p => p.Id).Returns("unavailable");
        mockUnavailable.Setup(p => p.Name).Returns("Unavailable Platform");
        mockUnavailable.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registry.RegisterPlatform(mockUnavailable.Object);

        // Act
        var available = await _registry.GetAvailablePlatformsAsync();

        // Assert
        Assert.Single(available);
        Assert.Contains(available, p => p.Id == "mock");
        Assert.DoesNotContain(available, p => p.Id == "unavailable");
    }

    [Fact]
    public void RegisterPlatform_Should_Throw_On_Duplicate_Id()
    {
        // Arrange
        var platform1 = new MockPlatformAdapter(_httpClient);
        var platform2 = new MockPlatformAdapter(_httpClient);
        _registry.RegisterPlatform(platform1);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _registry.RegisterPlatform(platform2));
    }

    [Fact]
    public void RegisterPlatform_Should_Throw_On_Null()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _registry.RegisterPlatform(null!));
    }

    [Fact]
    public void GetAllPlatforms_Should_Return_Empty_Initially()
    {
        // Act
        var platforms = _registry.GetAllPlatforms();

        // Assert
        Assert.Empty(platforms);
    }

    [Fact]
    public void RegisterPlatform_Should_Support_Multiple()
    {
        // Arrange
        var platform1 = new MockPlatformAdapter(_httpClient);

        var mockPlatform2 = new Mock<IModPlatform>();
        mockPlatform2.Setup(p => p.Id).Returns("another");
        mockPlatform2.Setup(p => p.Name).Returns("Another Platform");

        // Act
        _registry.RegisterPlatform(platform1);
        _registry.RegisterPlatform(mockPlatform2.Object);

        // Assert
        var platforms = _registry.GetAllPlatforms();
        Assert.Equal(2, platforms.Count);
    }

    [Fact]
    public async Task GetAvailablePlatforms_Should_Check_Availability()
    {
        // Arrange
        var mockPlatform = new Mock<IModPlatform>();
        mockPlatform.Setup(p => p.Id).Returns("test");
        mockPlatform.Setup(p => p.Name).Returns("Test Platform");
        mockPlatform.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _registry.RegisterPlatform(mockPlatform.Object);

        // Act
        var available = await _registry.GetAvailablePlatformsAsync();

        // Assert
        Assert.Single(available);
        mockPlatform.Verify(p => p.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
