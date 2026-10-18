using System.Diagnostics;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.PerformanceTests;

public sealed class LargeLibraryServicePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task TenThousandMods_RealRepositoriesAndOneHundredThousandSqlStatements()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LargeServicePerf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new HD2MimntDbContext(Path.Combine(root, "test.db"));
            await db.InitializeDatabaseAsync();
            var ids = await LargeLibraryWorkload.SeedAsync(db, root);
            var mods = new ModRepository(db);
            var profiles = new ProfileRepository(db);
            var modService = new ModService(mods, Path.Combine(root, "mods"));
            var profileService = new ProfileService(profiles, mods);
            var search = new SearchService(mods);
            var profile = (await profiles.GetActiveProfileAsync())!;
            var measurements = new List<object>();
            async Task Measure(string name, Func<Task> operation)
            {
                var times = new List<double>();
                var allocated = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < 7; i++)
                {
                    var timer = Stopwatch.StartNew();
                    await operation();
                    times.Add(timer.Elapsed.TotalMilliseconds);
                }
                measurements.Add(new { Name = name, FirstMs = times[0], WarmMs = LargeLibraryWorkload.Distribution(times.Skip(1)),
                    AllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d });
                output.WriteLine($"{name}: {string.Join(", ", times.Select(x => x.ToString("F2")))} ms");
            }
            await Measure("repository_get_all_10000", async () => Assert.Equal(10000, (await mods.GetAllAsync()).Count()));
            await Measure("repository_get_by_ids_10000", async () => Assert.Equal(10000, (await mods.GetByIdsAsync(ids)).Count()));
            await Measure("service_get_all_with_sidecars", async () => Assert.Equal(10000, (await modService.GetAllModsAsync()).Count()));
            await Measure("profiles_12_memberships_21000", async () => Assert.Equal(12, (await profiles.GetAllAsync()).Count()));
            await Measure("profile_normalize_10000", async () => Assert.Equal(10000, (await profileService.GetProfileByIdAsync(profile.Id))!.Mods.Count));
            await Measure("search_build_index_10000", () => search.BuildIndexAsync());
            await Measure("search_common_5000_results", async () => Assert.Equal(5000, (await search.SearchAsync("Armor")).Count()));
            await Measure("statistics_10000", async () => Assert.Equal(10000, (await modService.GetStatisticsAsync()).TotalCount));
            var enabled = true;
            await Measure("profile_toggle_1000", async () =>
            {
                enabled = !enabled;
                await profileService.SetModsEnabledAsync(profile.Id, ids.Take(1000).ToArray(), enabled);
                var saved = await profiles.GetByIdAsync(profile.Id);
                Assert.All(saved!.Mods.Take(1000), mod => Assert.Equal(enabled, mod.Enabled));
            });
            var flood = await LargeLibraryWorkload.FloodAsync(db.DatabasePath, ids);
            LargeLibraryWorkload.Save("large-library-services", new { DateUtc = DateTime.UtcNow, Runtime = Environment.Version.ToString(),
                ModCount = ids.Length, Profiles = 12, Memberships = 21000, Measurements = measurements, SqlFlood = flood });
        }
        finally { Directory.Delete(root, true); }
        Assert.False(Directory.Exists(root));
    }
}
