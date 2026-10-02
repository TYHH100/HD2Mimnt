using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests;

public class ProfileServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ProfileService _profileService;
    private readonly string _testDbPath;

    public ProfileServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _profileService = new ProfileService(_profileRepository, _modRepository);
    }

    [Fact]
    public async Task CreateProfile_ShouldCreateNewProfile()
    {
        // Act
        var profileId = await _profileService.CreateProfileAsync("Test Profile", "Test Description");

        // Assert
        Assert.NotEqual(Guid.Empty, profileId);

        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Equal("Test Profile", profile.Name);
        Assert.Equal("Test Description", profile.Description);
        Assert.False(profile.IsActive);
    }

    [Fact]
    public async Task SetActiveProfile_ShouldActivateProfile()
    {
        // Arrange
        var profileId1 = await _profileService.CreateProfileAsync("Profile 1");
        var profileId2 = await _profileService.CreateProfileAsync("Profile 2");

        // Act
        await _profileService.SetActiveProfileAsync(profileId1);

        // Assert
        var activeProfile = await _profileService.GetActiveProfileAsync();
        Assert.NotNull(activeProfile);
        Assert.Equal(profileId1, activeProfile.Id);

        // Act - switch to another profile
        await _profileService.SetActiveProfileAsync(profileId2);

        // Assert
        activeProfile = await _profileService.GetActiveProfileAsync();
        Assert.NotNull(activeProfile);
        Assert.Equal(profileId2, activeProfile.Id);
    }

    [Fact]
    public async Task AddModToProfile_ShouldAddMod()
    {
        // Arrange
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = @"C:\TestMods\TestMod"
        };
        await _modRepository.AddAsync(mod);
        var profileId = await _profileService.CreateProfileAsync("Test Profile");

        // Act
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // Assert
        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Single(profile.Mods);
        Assert.Equal(mod.Id, profile.Mods[0].ModId);
        Assert.True(profile.Mods[0].Enabled);
    }

    [Fact]
    public async Task RemoveModFromProfile_ShouldRemoveMod()
    {
        // Arrange
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = @"C:\TestMods\TestMod"
        };
        await _modRepository.AddAsync(mod);
        var profileId = await _profileService.CreateProfileAsync("Test Profile");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // Act
        await _profileService.RemoveModFromProfileAsync(profileId, mod.Id);

        // Assert
        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Empty(profile.Mods);
    }

    [Fact]
    public async Task SetModEnabled_ShouldToggleModState()
    {
        // Arrange
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = @"C:\TestMods\TestMod"
        };
        await _modRepository.AddAsync(mod);
        var profileId = await _profileService.CreateProfileAsync("Test Profile");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // Act
        await _profileService.SetModEnabledAsync(profileId, mod.Id, false);

        // Assert
        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.False(profile.Mods[0].Enabled);
    }

    [Fact]
    public async Task DuplicateProfile_ShouldCreateCopyWithMods()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Version = "1.0.0", Path = @"C:\TestMods\Mod1" };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Version = "1.0.0", Path = @"C:\TestMods\Mod2" };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        var profileId = await _profileService.CreateProfileAsync("Original Profile");
        await _profileService.AddModToProfileAsync(profileId, mod1.Id);
        await _profileService.AddModToProfileAsync(profileId, mod2.Id);

        // Act
        var duplicatedProfile = await _profileService.DuplicateProfileAsync(profileId, "Copy of Profile");

        // Assert
        Assert.NotEqual(profileId, duplicatedProfile.Id);
        Assert.Equal("Copy of Profile", duplicatedProfile.Name);
        Assert.Equal(2, duplicatedProfile.Mods.Count);
        Assert.False(duplicatedProfile.IsActive);
    }

    [Fact]
    public async Task ReorderMods_ShouldChangeModOrder()
    {
        // Arrange
        var mod1 = new Mod { Id = Guid.NewGuid(), Name = "Mod 1", Version = "1.0.0", Path = @"C:\TestMods\Mod1" };
        var mod2 = new Mod { Id = Guid.NewGuid(), Name = "Mod 2", Version = "1.0.0", Path = @"C:\TestMods\Mod2" };
        var mod3 = new Mod { Id = Guid.NewGuid(), Name = "Mod 3", Version = "1.0.0", Path = @"C:\TestMods\Mod3" };
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);
        await _modRepository.AddAsync(mod3);

        var profileId = await _profileService.CreateProfileAsync("Test Profile");
        await _profileService.AddModToProfileAsync(profileId, mod1.Id);
        await _profileService.AddModToProfileAsync(profileId, mod2.Id);
        await _profileService.AddModToProfileAsync(profileId, mod3.Id);

        // Act - reorder: 3, 1, 2
        await _profileService.ReorderModsAsync(profileId, new List<Guid> { mod3.Id, mod1.Id, mod2.Id });

        // Assert
        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        var orderedMods = profile.Mods.OrderBy(m => m.Order).ToList();
        Assert.Equal(mod3.Id, orderedMods[0].ModId);
        Assert.Equal(mod1.Id, orderedMods[1].ModId);
        Assert.Equal(mod2.Id, orderedMods[2].ModId);
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
