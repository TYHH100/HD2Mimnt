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
    public async Task Initialize_ShouldCreateOneProtectedDefaultAndRestorePersistedActiveProfile()
    {
        await _profileService.InitializeAsync();
        await _profileService.InitializeAsync();
        var defaultProfile = Assert.Single(await _profileService.GetAllProfilesAsync());
        Assert.True(defaultProfile.IsDefault);
        Assert.True(defaultProfile.IsActive);
        var otherId = await _profileService.CreateProfileAsync("我的配置");
        await _profileService.SetActiveProfileAsync(otherId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _profileService.DeleteProfileAsync(defaultProfile.Id));
        var duplicate = await _profileService.DuplicateProfileAsync(defaultProfile.Id, "默认副本");
        Assert.False(duplicate.IsDefault);
        using var reopened = new HD2MimntDbContext(_testDbPath);
        await reopened.InitializeDatabaseAsync();
        var restored = new ProfileService(new ProfileRepository(reopened), new ModRepository(reopened));
        await restored.InitializeAsync();
        Assert.Equal(otherId, (await restored.GetActiveProfileAsync())!.Id);
        Assert.Single((await restored.GetAllProfilesAsync()).Where(profile => profile.IsDefault));
        Assert.Equal(defaultProfile.Id, (await restored.GetAllProfilesAsync()).First().Id);
        await restored.ReorderProfilesAsync(new[] { otherId, defaultProfile.Id, duplicate.Id });
        Assert.Equal(defaultProfile.Id, (await restored.GetAllProfilesAsync()).First().Id);
    }

    [Fact]
    public async Task Initialize_ShouldMigrateLegacyDefaultWithoutReplacingActiveConfiguration()
    {
        var originalId = await _profileService.CreateProfileAsync("旧默认");
        var activeId = await _profileService.CreateProfileAsync("上次使用");
        await _profileService.SetActiveProfileAsync(activeId);
        using (var command = (await _dbContext.GetConnectionAsync()).CreateCommand())
        {
            command.CommandText = "ALTER TABLE Profiles DROP COLUMN IsDefault;";
            await command.ExecuteNonQueryAsync();
        }
        await _dbContext.InitializeDatabaseAsync();
        await _profileService.InitializeAsync();
        Assert.True((await _profileService.GetProfileByIdAsync(originalId))!.IsDefault);
        Assert.Equal(activeId, (await _profileService.GetActiveProfileAsync())!.Id);
        Assert.Equal(2, (await _profileService.GetAllProfilesAsync()).Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _profileService.DeleteProfileAsync(originalId));
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
    public async Task SetActiveProfile_ShouldRejectUnknownProfileWithoutClearingCurrentProfile()
    {
        var profileId = await _profileService.CreateProfileAsync("Profile 1");
        await _profileService.SetActiveProfileAsync(profileId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _profileService.SetActiveProfileAsync(Guid.NewGuid()));

        var activeProfile = await _profileService.GetActiveProfileAsync();
        Assert.NotNull(activeProfile);
        Assert.Equal(profileId, activeProfile.Id);
    }

    [Fact]
    public async Task DeleteProfile_ShouldRejectActiveProfile()
    {
        var profileId = await _profileService.CreateProfileAsync("Profile 1");
        await _profileService.SetActiveProfileAsync(profileId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _profileService.DeleteProfileAsync(profileId));

        Assert.NotNull(await _profileRepository.GetByIdAsync(profileId));
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
    public async Task ProfileSeparator_ShouldPersistRenameCollapseAndReload()
    {
        var profileId = await _profileService.CreateProfileAsync("分组配置");
        var separator = await _profileService.AddSeparatorAsync(profileId, "武器", 0);
        await _profileService.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
        await _profileService.RenameSeparatorAsync(profileId, separator.Id, "武器与护甲");
        await _profileService.UpdateSeparatorAsync(profileId, separator.Id, "武器与护甲", "#aa33ee");
        await _profileService.UpdateSeparatorAsync(profileId, Guid.Empty, "", "#12ab56");
        await _profileService.SetSeparatorCollapsedAsync(profileId, Guid.Empty, true);

        using var reopened = new HD2MimntDbContext(_testDbPath);
        await reopened.InitializeDatabaseAsync();
        var loaded = await new ProfileRepository(reopened).GetByIdAsync(profileId);
        var saved = Assert.Single(loaded!.Separators);
        Assert.Equal(separator.Id, saved.Id);
        Assert.Equal("武器与护甲", saved.Name);
        Assert.True(saved.IsCollapsed);
        Assert.Equal("#AA33EE", saved.Color);
        Assert.Equal("#12AB56", loaded.LuaSeparatorColor);
        Assert.True(loaded.IsLuaGroupCollapsed);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _profileService.UpdateSeparatorAsync(profileId, separator.Id, "错误", "#00GG11"));
        Assert.Equal("武器与护甲", (await _profileService.GetProfileByIdAsync(profileId))!.Separators[0].Name);
        await _profileService.UpdateSeparatorAsync(profileId, Guid.Empty, "", "");
        Assert.Empty((await _profileService.GetProfileByIdAsync(profileId))!.LuaSeparatorColor);
    }

    [Fact]
    public async Task ProfileSeparators_ShouldUpgradeLegacyTableDuplicateAndDeleteWithoutRemovingMods()
    {
        var profileId = await _profileService.CreateProfileAsync("原配置");
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Mod", Version = "1", Path = @"C:\TestMods\Mod" };
        await _modRepository.AddAsync(mod);
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        using (var command = (await _dbContext.GetConnectionAsync()).CreateCommand())
        {
            command.CommandText = "ALTER TABLE ProfileSeparators DROP COLUMN Color; ALTER TABLE Profiles DROP COLUMN LuaSeparatorColor; ALTER TABLE Profiles DROP COLUMN IsLuaGroupCollapsed";
            await command.ExecuteNonQueryAsync();
        }
        await _dbContext.InitializeDatabaseAsync();
        var separator = await _profileService.AddSeparatorAsync(profileId, "武器", 0, "#FF2255");
        await _profileService.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
        await _profileService.UpdateSeparatorAsync(profileId, Guid.Empty, "", "#44AA77");
        await _profileService.SetSeparatorCollapsedAsync(profileId, Guid.Empty, true);
        var duplicate = await _profileService.DuplicateProfileAsync(profileId, "副本");
        var copied = Assert.Single(duplicate.Separators);
        Assert.NotEqual(separator.Id, copied.Id);
        Assert.Equal(duplicate.Id, copied.ProfileId);
        Assert.True(copied.IsCollapsed);
        Assert.Equal("#FF2255", copied.Color);
        Assert.Equal("#44AA77", duplicate.LuaSeparatorColor);
        Assert.True(duplicate.IsLuaGroupCollapsed);
        await _profileService.RemoveSeparatorAsync(duplicate.Id, copied.Id);
        Assert.Single((await _profileService.GetProfileByIdAsync(duplicate.Id))!.Mods);
        await _profileService.DeleteProfileAsync(profileId);
        using var check = (await _dbContext.GetConnectionAsync()).CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM ProfileSeparators WHERE ProfileId = @Id";
        check.Parameters.AddWithValue("@Id", profileId.ToString());
        Assert.Equal(0L, await check.ExecuteScalarAsync());
        Assert.NotNull(await _modRepository.GetByIdAsync(mod.Id));
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
    public async Task SetModEnabled_ShouldAddMissingModWhenEnabling()
    {
        var mod = new Mod { Id = Guid.NewGuid(), Name = "New Mod", Version = "1.0.0", Path = @"C:\TestMods\NewMod" };
        await _modRepository.AddAsync(mod);
        var profileId = await _profileService.CreateProfileAsync("Test Profile");

        await _profileService.SetModEnabledAsync(profileId, mod.Id, true);

        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        var profileMod = Assert.Single(profile.Mods);
        Assert.Equal(mod.Id, profileMod.ModId);
        Assert.True(profileMod.Enabled);
    }

    [Fact]
    public async Task SetModsEnabled_ShouldPersistBatchInOneProfileUpdate()
    {
        var mods = Enumerable.Range(1, 3)
            .Select(index => new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Mod {index}",
                Version = "1.0.0",
                Path = $@"C:\TestMods\Mod{index}"
            })
            .ToArray();
        foreach (var mod in mods) await _modRepository.AddAsync(mod);
        var profileId = await _profileService.CreateProfileAsync("Test Profile");

        await _profileService.SetModsEnabledAsync(profileId, mods.Select(mod => mod.Id).ToArray(), true);

        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Equal(mods.Select(mod => mod.Id), profile.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));
        Assert.All(profile.Mods, mod => Assert.True(mod.Enabled));
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
