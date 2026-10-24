using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// Core service performance tests - tests the actual bottlenecks without UI complexity
/// </summary>
public class ServicePerformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly PerformanceReport _report;

    public ServicePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _report = new PerformanceReport
        {
            StartTime = DateTime.UtcNow,
            Environment = $"Windows 11, .NET 10, {Environment.ProcessorCount} cores"
        };
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(8000)]
    [InlineData(10000)]
    public async Task ModRepository_GetAllMods_Performance(int modCount)
    {
        using var env = new TestEnvironment($"GetAll_{modCount}");
        var mods = TestDataGenerator.GenerateMods(modCount);
        await env.PopulateModsAsync(mods);

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);

        using (var timer = new PerformanceTimer($"GetAllMods", modCount, _report.Add))
        {
            var result = await modService.GetAllModsAsync();
            timer.SetResultCount(result.Count());
        }

        _output.WriteLine($"GetAllMods({modCount}): {_report.Measurements.Last()}");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(8000)]
    [InlineData(10000)]
    public async Task SearchService_Search_Performance(int modCount)
    {
        using var env = new TestEnvironment($"Search_{modCount}");
        var mods = TestDataGenerator.GenerateMods(modCount);
        await env.PopulateModsAsync(mods);

        var searchService = new SearchService(env.ModRepository, null);
        await searchService.BuildIndexAsync();

        // Test search for common term
        using (var timer = new PerformanceTimer($"Search 'Enhanced'", modCount, _report.Add))
        {
            var results = await searchService.SearchAsync("Enhanced");
            timer.SetResultCount(results.Count());
        }

        // Test search for author
        using (var timer = new PerformanceTimer($"Search 'ModderPro'", modCount, _report.Add))
        {
            var results = await searchService.SearchAsync("ModderPro");
            timer.SetResultCount(results.Count());
        }

        _output.WriteLine($"Search tests completed for {modCount} mods");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(8000)]
    public async Task ProfileService_BulkOperations_Performance(int modCount)
    {
        using var env = new TestEnvironment($"Profile_{modCount}");
        var mods = TestDataGenerator.GenerateMods(modCount);
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

        // Test adding mods to profile
        var modsToAdd = mods.Take(100).ToList();
        using (var timer = new PerformanceTimer($"Add 100 mods to profile", modCount, _report.Add))
        {
            foreach (var mod in modsToAdd)
            {
                await profileService.AddModToProfileAsync(profile.Id, mod.Id);
            }
            timer.SetResultCount(100);
        }

        // Test bulk enable
        var modIds = modsToAdd.Select(m => m.Id).ToList();
        using (var timer = new PerformanceTimer($"Bulk enable 100 mods", modCount, _report.Add))
        {
            await profileService.SetModsEnabledAsync(profile.Id, modIds, true);
            timer.SetResultCount(100);
        }

        // Test bulk disable
        using (var timer = new PerformanceTimer($"Bulk disable 100 mods", modCount, _report.Add))
        {
            await profileService.SetModsEnabledAsync(profile.Id, modIds, false);
            timer.SetResultCount(100);
        }

        _output.WriteLine($"Profile bulk operations completed");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(8000)]
    public async Task ModService_GetStatistics_Performance(int modCount)
    {
        using var env = new TestEnvironment($"Stats_{modCount}");
        var mods = TestDataGenerator.GenerateMods(modCount);
        await env.PopulateModsAsync(mods);

        var modService = new ModService(env.ModRepository, env.ModStoragePath, null);

        using (var timer = new PerformanceTimer($"GetStatistics", modCount, _report.Add))
        {
            var stats = await modService.GetStatisticsAsync();
            timer.SetResultCount(stats.TotalCount);
        }

        _output.WriteLine($"GetStatistics({modCount}): {_report.Measurements.Last()}");
    }

    [Fact]
    public void GeneratePerformanceReport()
    {
        _report.EndTime = DateTime.UtcNow;
        _report.PrintSummary();

        var markdown = _report.GenerateMarkdownReport();
        _output.WriteLine("\n" + markdown);
    }
}
