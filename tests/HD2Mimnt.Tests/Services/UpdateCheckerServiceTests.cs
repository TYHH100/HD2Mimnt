using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

public class UpdateCheckerServiceTests
{
    private readonly Mock<IPlatformRegistry> _mockRegistry;
    private readonly Mock<IModRepository> _mockRepository;
    private readonly Mock<IModPlatform> _mockPlatform;
    private readonly UpdateCheckerService _service;

    public UpdateCheckerServiceTests()
    {
        _mockRegistry = new Mock<IPlatformRegistry>();
        _mockRepository = new Mock<IModRepository>();
        _mockPlatform = new Mock<IModPlatform>();
        _service = new UpdateCheckerService(_mockRegistry.Object, _mockRepository.Object);
    }

    [Fact]
    public void Constructor_Should_Throw_When_Registry_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateCheckerService(null!, _mockRepository.Object));
    }

    [Fact]
    public void Constructor_Should_Throw_When_Repository_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateCheckerService(_mockRegistry.Object, null!));
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Return_Null_When_Mod_Not_Found()
    {
        // Arrange
        _mockRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Core.Entities.Mod?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CheckUpdateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Return_Null_When_No_Platform_Info()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = null,
            PlatformModId = null
        };

        _mockRepository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);

        // Act
        var result = await _service.CheckUpdateAsync(mod.Id);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Return_Null_When_Platform_Not_Available()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        _mockRepository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);
        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns((IModPlatform?)null);

        // Act
        var result = await _service.CheckUpdateAsync(mod.Id);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Detect_Update_When_Versions_Differ()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        var platformModInfo = new PlatformModInfo
        {
            Id = "mod1",
            Name = "Test Mod",
            Version = "2.0.0",
            UpdatedAt = DateTime.UtcNow
        };

        SetupMocks(mod, platformModInfo);

        // Act
        var result = await _service.CheckUpdateAsync(mod.Id);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.HasUpdate);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("2.0.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Not_Detect_Update_When_Versions_Match()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        var platformModInfo = new PlatformModInfo
        {
            Id = "mod1",
            Name = "Test Mod",
            Version = "1.0.0"
        };

        SetupMocks(mod, platformModInfo);

        // Act
        var result = await _service.CheckUpdateAsync(mod.Id);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.HasUpdate);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckUpdateAsync_Should_Update_LastUpdateCheck()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1",
            LastUpdateCheck = null
        };

        var platformModInfo = new PlatformModInfo
        {
            Id = "mod1",
            Name = "Test Mod",
            Version = "1.0.0"
        };

        SetupMocks(mod, platformModInfo);

        // Act
        await _service.CheckUpdateAsync(mod.Id);

        // Assert
        _mockRepository.Verify(r => r.UpdateAsync(
            It.Is<Core.Entities.Mod>(m => m.LastUpdateCheck.HasValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAllUpdatesAsync_Should_Return_Only_Mods_With_Updates()
    {
        // Arrange
        var mod1 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 1",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        var mod2 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 2",
            Version = "2.0.0",
            PlatformId = "test",
            PlatformModId = "mod2"
        };

        var mod3 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 3",
            Version = "1.0.0",
            PlatformId = null,
            PlatformModId = null
        };

        _mockRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Core.Entities.Mod> { mod1, mod2, mod3 });

        _mockRepository.Setup(r => r.GetByIdAsync(mod1.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod1);

        _mockRepository.Setup(r => r.GetByIdAsync(mod2.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod2);

        _mockRepository.Setup(r => r.GetByIdAsync(mod3.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod3);

        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns(_mockPlatform.Object);

        _mockPlatform.Setup(p => p.GetModInfoAsync("mod1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformModInfo { Version = "2.0.0" }); // Update available

        _mockPlatform.Setup(p => p.GetModInfoAsync("mod2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformModInfo { Version = "2.0.0" }); // No update

        _mockRepository.Setup(r => r.UpdateAsync(It.IsAny<Core.Entities.Mod>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var updates = await _service.CheckAllUpdatesAsync();

        // Assert
        Assert.Single(updates); // Only mod1 has update
        Assert.Equal(mod1.Id, updates[0].ModId);
    }

    [Fact]
    public async Task GetModsNeedingUpdateCheckAsync_Should_Return_Unchecked_Mods()
    {
        // Arrange
        var mod1 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 1",
            PlatformId = "test",
            PlatformModId = "mod1",
            LastUpdateCheck = DateTime.UtcNow.AddDays(-2)
        };

        var mod2 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 2",
            PlatformId = "test",
            PlatformModId = "mod2",
            LastUpdateCheck = DateTime.UtcNow.AddHours(-1)
        };

        var mod3 = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Mod 3",
            PlatformId = "test",
            PlatformModId = "mod3",
            LastUpdateCheck = null
        };

        _mockRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Core.Entities.Mod> { mod1, mod2, mod3 });

        // Act
        var mods = await _service.GetModsNeedingUpdateCheckAsync(TimeSpan.FromDays(1));

        // Assert
        Assert.Equal(2, mods.Count); // mod1 (old) and mod3 (never checked)
        Assert.Contains(mods, m => m.Id == mod1.Id);
        Assert.Contains(mods, m => m.Id == mod3.Id);
    }

    [Fact]
    public async Task GetChangelogAsync_Should_Return_Changelog()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        var versions = new List<PlatformModVersion>
        {
            new PlatformModVersion
            {
                Version = "2.0.0",
                ChangeLog = "Version 2.0.0 changelog"
            },
            new PlatformModVersion
            {
                Version = "1.0.0",
                ChangeLog = "Version 1.0.0 changelog"
            }
        };

        _mockRepository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);
        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetVersionsAsync("mod1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions);

        // Act
        var changelog = await _service.GetChangelogAsync(mod.Id, "2.0.0");

        // Assert
        Assert.Equal("Version 2.0.0 changelog", changelog);
    }

    [Fact]
    public async Task GetChangelogAsync_Should_Return_Null_When_Version_Not_Found()
    {
        // Arrange
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            PlatformId = "test",
            PlatformModId = "mod1"
        };

        var versions = new List<PlatformModVersion>
        {
            new PlatformModVersion { Version = "1.0.0", ChangeLog = "Changelog" }
        };

        _mockRepository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);
        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetVersionsAsync("mod1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions);

        // Act
        var changelog = await _service.GetChangelogAsync(mod.Id, "999.0.0");

        // Assert
        Assert.Null(changelog);
    }

    private void SetupMocks(Core.Entities.Mod mod, PlatformModInfo platformModInfo)
    {
        _mockRepository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mod);
        _mockRegistry.Setup(r => r.GetPlatform(mod.PlatformId!)).Returns(_mockPlatform.Object);
        _mockPlatform.Setup(p => p.GetModInfoAsync(mod.PlatformModId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(platformModInfo);
        _mockRepository.Setup(r => r.UpdateAsync(It.IsAny<Core.Entities.Mod>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}
