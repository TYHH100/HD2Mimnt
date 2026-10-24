using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// End-to-end business workflow tests
/// Tests core business operations without UI layer
/// </summary>
public class CoreE2ETests
{
    private readonly ITestOutputHelper _output;

    public CoreE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ProfileStateIsolation_MultipleProfiles()
    {
        using var env = new TestEnvironment("ProfileIsolation");
        var mods = TestDataGenerator.GenerateMods(50);
        await env.PopulateModsAsync(mods);

        var profileA = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Profile A",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        var profileB = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Profile B",
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        await env.PopulateProfilesAsync(new List<Profile> { profileA, profileB });

        var profileService = new ProfileService(env.ProfileRepository, env.ModRepository);

        // Profile A: Mod 0 enabled, Mod 1 disabled
        await profileService.AddModToProfileAsync(profileA.Id, mods[0].Id);
        await profileService.AddModToProfileAsync(profileA.Id, mods[1].Id);
        await profileService.SetModEnabledAsync(profileA.Id, mods[0].Id, true);
        await profileService.SetModEnabledAsync(profileA.Id, mods[1].Id, false);

        // Profile B: Mod 0 disabled, Mod 2 enabled
        await profileService.AddModToProfileAsync(profileB.Id, mods[0].Id);
        await profileService.AddModToProfileAsync(profileB.Id, mods[2].Id);
        await profileService.SetModEnabledAsync(profileB.Id, mods[0].Id, false);
        await profileService.SetModEnabledAsync(profileB.Id, mods[2].Id, true);

        // Verify Profile A state
        var profileAReloaded = await profileService.GetProfileByIdAsync(profileA.Id);
        Assert.NotNull(profileAReloaded);
        Assert.Equal(2, profileAReloaded.Mods.Count);
        Assert.True(profileAReloaded.Mods.First(m => m.ModId == mods[0].Id).Enabled);
        Assert.False(profileAReloaded.Mods.First(m => m.ModId == mods[1].Id).Enabled);

        // Verify Profile B state
        var profileBReloaded = await profileService.GetProfileByIdAsync(profileB.Id);
        Assert.NotNull(profileBReloaded);
        Assert.Equal(2, profileBReloaded.Mods.Count);
        Assert.False(profileBReloaded.Mods.First(m => m.ModId == mods[0].Id).Enabled);
        Assert.True(profileBReloaded.Mods.First(m => m.ModId == mods[2].Id).Enabled);

        // Enable Mod 0 in Profile B
        await profileService.SetModEnabledAsync(profileB.Id, mods[0].Id, true);

        // Verify Profile A state hasn't changed
        profileAReloaded = await profileService.GetProfileByIdAsync(profileA.Id);
        Assert.NotNull(profileAReloaded);
        Assert.True(profileAReloaded.Mods.First(m => m.ModId == mods[0].Id).Enabled);
        Assert.Equal(2, profileAReloaded.Mods.Count);

        _output.WriteLine("✓ Profile state isolation verified");
    }

    [Fact]
    public async Task RemoveFromProfile_DoesNotDeleteFromLibrary()
    {
        using var env = new TestEnvironment("RemoveFromProfile");
        var mods = TestDataGenerator.GenerateMods(10);
        await env.PopulateModsAsync(mods);

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        await env.PopulateProfilesAsync(new List<Profile> { profile });

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);
        var profileService = new ProfileService(env.ProfileRepository, env.ModRepository);

        // Add mod to profile
        await profileService.AddModToProfileAsync(profile.Id, mods[0].Id);

        // Verify mod is in profile
        var profileReloaded = await profileService.GetProfileByIdAsync(profile.Id);
        Assert.NotNull(profileReloaded);
        Assert.Single(profileReloaded.Mods);

        // Remove from profile
        await profileService.RemoveModFromProfileAsync(profile.Id, mods[0].Id);

        // Verify still in global library
        var allMods = await modService.GetAllModsAsync();
        Assert.Equal(10, allMods.Count());
        Assert.Contains(allMods, m => m.Id == mods[0].Id);

        // Verify not in profile
        profileReloaded = await profileService.GetProfileByIdAsync(profile.Id);
        Assert.NotNull(profileReloaded);
        Assert.Empty(profileReloaded.Mods);

        _output.WriteLine("✓ Remove from profile does not delete from library");
    }

    [Fact]
    public async Task DeleteFromLibrary_RemovesFromAllProfiles()
    {
        using var env = new TestEnvironment("DeleteFromLibrary");
        var mods = TestDataGenerator.GenerateMods(10);
        await env.PopulateModsAsync(mods);

        var profileA = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Profile A",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        var profileB = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Profile B",
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        await env.PopulateProfilesAsync(new List<Profile> { profileA, profileB });

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);
        var profileService = new ProfileService(env.ProfileRepository, env.ModRepository);

        // Add mod to both profiles
        await profileService.AddModToProfileAsync(profileA.Id, mods[0].Id);
        await profileService.AddModToProfileAsync(profileB.Id, mods[0].Id);

        // Verify initial state
        var allMods = await modService.GetAllModsAsync();
        Assert.Equal(10, allMods.Count());

        // Delete from library
        await modService.DeleteModAsync(mods[0].Id);

        // Verify removed from library
        allMods = await modService.GetAllModsAsync();
        Assert.Equal(9, allMods.Count());
        Assert.DoesNotContain(allMods, m => m.Id == mods[0].Id);

        // Verify removed from Profile A
        var profileAReloaded = await profileService.GetProfileByIdAsync(profileA.Id);
        Assert.NotNull(profileAReloaded);
        Assert.Empty(profileAReloaded.Mods);

        // Verify removed from Profile B
        var profileBReloaded = await profileService.GetProfileByIdAsync(profileB.Id);
        Assert.NotNull(profileBReloaded);
        Assert.Empty(profileBReloaded.Mods);

        _output.WriteLine("✓ Delete from library removes from all profiles");
    }

    [Fact]
    public async Task BulkEnableDisable_WorksCorrectly()
    {
        using var env = new TestEnvironment("BulkEnableDisable");
        var mods = TestDataGenerator.GenerateMods(100);
        await env.PopulateModsAsync(mods);

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        await env.PopulateProfilesAsync(new List<Profile> { profile });

        var profileService = new ProfileService(env.ProfileRepository, env.ModRepository);

        // Add 50 mods to profile
        for (int i = 0; i < 50; i++)
        {
            await profileService.AddModToProfileAsync(profile.Id, mods[i].Id);
        }

        // Bulk enable first 30 (they should already be in profile from AddModToProfileAsync)
        var modIdsToEnable = mods.Take(30).Select(m => m.Id).ToList();
        await profileService.SetModsEnabledAsync(profile.Id, modIdsToEnable, true);

        var profileReloaded = await profileService.GetProfileByIdAsync(profile.Id);
        Assert.NotNull(profileReloaded);

        // All 50 should be in profile, but only first 30 should be enabled
        Assert.Equal(50, profileReloaded.Mods.Count);
        var enabledCount = profileReloaded.Mods.Count(m => m.Enabled && modIdsToEnable.Contains(m.ModId));
        Assert.Equal(30, enabledCount);

        // Bulk disable all 50
        var allModIds = mods.Take(50).Select(m => m.Id).ToList();
        await profileService.SetModsEnabledAsync(profile.Id, allModIds, false);

        profileReloaded = await profileService.GetProfileByIdAsync(profile.Id);
        Assert.NotNull(profileReloaded);

        // All should still be in profile, but none enabled
        Assert.Equal(50, profileReloaded.Mods.Count);
        enabledCount = profileReloaded.Mods.Count(m => m.Enabled);
        Assert.Equal(0, enabledCount);

        _output.WriteLine("✓ Bulk enable/disable works correctly");
    }

    [Fact]
    public async Task Search_FindsCorrectResults()
    {
        using var env = new TestEnvironment("SearchCorrectness");
        var mods = TestDataGenerator.GenerateMods(1000);
        await env.PopulateModsAsync(mods);

        var searchService = new SearchService(env.ModRepository, null);
        await searchService.BuildIndexAsync();

        // Search for "Enhanced"
        var results = await searchService.SearchAsync("Enhanced");
        var resultList = results.ToList();

        // All results should contain "Enhanced" in name, description, or author
        foreach (var mod in resultList)
        {
            bool contains = mod.Name.Contains("Enhanced", StringComparison.OrdinalIgnoreCase) ||
                          mod.Description.Contains("Enhanced", StringComparison.OrdinalIgnoreCase) ||
                          mod.Author.Contains("Enhanced", StringComparison.OrdinalIgnoreCase);
            Assert.True(contains, $"Mod {mod.Name} does not contain 'Enhanced'");
        }

        _output.WriteLine($"✓ Search found {resultList.Count} correct results");
    }

    [Fact]
    public async Task EmptyLibrary_HandlesCorrectly()
    {
        using var env = new TestEnvironment("EmptyLibrary");

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Empty Profile",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        await env.PopulateProfilesAsync(new List<Profile> { profile });

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);
        var profileService = new ProfileService(env.ProfileRepository, env.ModRepository);

        // Empty library
        var allMods = await modService.GetAllModsAsync();
        Assert.Empty(allMods);

        var stats = await modService.GetStatisticsAsync();
        Assert.Equal(0, stats.TotalCount);

        // Add some mods
        var mods = TestDataGenerator.GenerateMods(10);
        await env.PopulateModsAsync(mods);

        allMods = await modService.GetAllModsAsync();
        Assert.Equal(10, allMods.Count());

        stats = await modService.GetStatisticsAsync();
        Assert.Equal(10, stats.TotalCount);

        _output.WriteLine("✓ Empty library handled correctly");
    }

    [Fact]
    public async Task MemoryStability_RepeatedOperations()
    {
        using var env = new TestEnvironment("MemoryStability");
        var mods = TestDataGenerator.GenerateMods(1000);
        await env.PopulateModsAsync(mods);

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        await env.PopulateProfilesAsync(new List<Profile> { profile });

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);
        var searchService = new SearchService(env.ModRepository, null);
        await searchService.BuildIndexAsync();

        var memoryBefore = GC.GetTotalMemory(true);

        // Repeat operations 5 times
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var allMods = await modService.GetAllModsAsync();
            var stats = await modService.GetStatisticsAsync();
            var searchResults = await searchService.SearchAsync("Enhanced");
            var searchResults2 = await searchService.SearchAsync("ModderPro");
        }

        var memoryAfter = GC.GetTotalMemory(true);
        var memoryGrowth = memoryAfter - memoryBefore;

        _output.WriteLine($"Memory growth after 5 iterations: {memoryGrowth / 1024}KB");
        _output.WriteLine($"Before: {memoryBefore / 1024}KB, After: {memoryAfter / 1024}KB");

        // Memory should not grow excessively (allow 15MB growth for 5 iterations)
        Assert.True(memoryGrowth < 15 * 1024 * 1024,
            $"Memory grew by {memoryGrowth / 1024 / 1024}MB, expected < 15MB");

        _output.WriteLine("✓ Memory stability verified");
    }
}
