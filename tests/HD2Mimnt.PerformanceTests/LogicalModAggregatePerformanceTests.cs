using System.Diagnostics;
using System.Text.Json;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// LogicalMod/Deployment 聚合读取的 N+1 修复（批量 IN 查询）前后对比样本。
/// seed 走纯 SQL：新旧仓储实现读到的数据完全一致，只对比读路径成本。
/// </summary>
public sealed class LogicalModAggregatePerformanceTests(ITestOutputHelper output)
{
    private const int ModCount = 500;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task FiveHundredLogicalMods_AggregateReads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LogicalModPerf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new HD2MimntDbContext(Path.Combine(root, "test.db"));
            await db.InitializeDatabaseAsync();
            var sessionId = await SeedAsync(db);

            var logicalMods = new LogicalModRepository(db);
            var deployments = new DeploymentRepository(db);
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
                measurements.Add(new { Name = name, FirstMs = times[0],
                    WarmMs = LargeLibraryWorkload.Distribution(times.Skip(1)),
                    AllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d });
                output.WriteLine($"{name}: {string.Join(", ", times.Select(x => x.ToString("F2")))} ms");
            }

            await Measure("logical_mods_get_all_500", async () =>
                Assert.Equal(ModCount, (await logicalMods.GetAllAsync()).Count));
            await Measure("logical_mods_aggregate_4500_children", async () =>
                Assert.Equal(ModCount * 9, (await logicalMods.GetAllAsync())
                    .Sum(m => m.Components.Count + m.Components.Sum(c => c.DeployableFiles.Count) + m.Packages.Count)));
            await Measure("logical_mods_search_250", async () =>
                Assert.Equal(ModCount / 2, (await logicalMods.SearchByNameAsync("Match")).Count));
            await Measure("deployments_by_session_200_records", async () =>
            {
                var records = await deployments.GetBySessionIdAsync(sessionId);
                Assert.Equal(200, records.Count());
                Assert.Equal(2000, records.Sum(r => r.Files.Count));
            });

            LargeLibraryWorkload.Save("logical-mod-aggregates", new { DateUtc = DateTime.UtcNow,
                Runtime = Environment.Version.ToString(), LogicalMods = ModCount,
                ComponentsPerMod = 2, FilesPerComponent = 3, PackagesPerMod = 1,
                DeploymentRecords = 200, FilesPerDeployment = 10, Measurements = measurements });
        }
        finally { Directory.Delete(root, true); }
        Assert.False(Directory.Exists(root));
    }

    private static async Task<Guid> SeedAsync(HD2MimntDbContext db)
    {
        using var connection = await db.OpenConnectionAsync();
        var sessionId = Guid.NewGuid();
        var date = DateTime.UtcNow.ToString("O");
        using (var transaction = connection.BeginTransaction())
        {
            using (var profile = connection.CreateCommand())
            {
                profile.Transaction = transaction;
                profile.CommandText = "INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES ($id, 'Perf', $date, $date, 1)";
                profile.Parameters.AddWithValue("$id", sessionId.ToString());
                profile.Parameters.AddWithValue("$date", date);
                profile.ExecuteNonQuery();
            }

            using (var mod = connection.CreateCommand())
            {
                mod.Transaction = transaction;
                mod.CommandText = "INSERT INTO LogicalMods (Id, Name, Version, Author, Description, ManifestVersion, CreatedAt, ModifiedAt, TotalSize, ThumbnailPath, Tags, Metadata) VALUES ($id, $name, '1.0.0', $author, '性能样本', 0, $date, $date, 1048576, NULL, '[]', '{}')";
                mod.Parameters.AddWithValue("$id", ""); mod.Parameters.AddWithValue("$name", "");
                mod.Parameters.AddWithValue("$author", ""); mod.Parameters.AddWithValue("$date", date);
                using (var pkg = connection.CreateCommand())
                {
                    pkg.Transaction = transaction;
                    pkg.CommandText = "INSERT INTO PackageSources (Id, LogicalModId, Name, PhysicalPath, ArchiveType, Size, Hash, LastModified, ParentPackageId, Depth, PathInParent) VALUES ($id, $mod, $name, 'pkg.zip', 0, 1048576, $hash, $date, NULL, 0, NULL)";
                    pkg.Parameters.AddWithValue("$id", ""); pkg.Parameters.AddWithValue("$mod", "");
                    pkg.Parameters.AddWithValue("$name", ""); pkg.Parameters.AddWithValue("$hash", "");
                    pkg.Parameters.AddWithValue("$date", date);
                    using (var component = connection.CreateCommand())
                    {
                        component.Transaction = transaction;
                        component.CommandText = "INSERT INTO LogicalModComponents (Id, LogicalModId, ComponentId, Type, Name, SourcePackageId, Size, Enabled, Dependencies, Metadata) VALUES ($id, $mod, $comp, 2, $name, $pkg, 512000, 1, '[]', '{}')";
                        component.Parameters.AddWithValue("$id", ""); component.Parameters.AddWithValue("$mod", "");
                        component.Parameters.AddWithValue("$comp", ""); component.Parameters.AddWithValue("$name", "");
                        component.Parameters.AddWithValue("$pkg", ""); 
                        using (var file = connection.CreateCommand())
                        {
                            file.Transaction = transaction;
                            file.CommandText = "INSERT INTO DeployableFiles (Id, ComponentId, SourcePath, TargetPath, Size, Hash, PackageId, IsShared, Metadata) VALUES ($id, $component, $src, $dst, 170000, $hash, $pkg, 0, '{}')";
                            file.Parameters.AddWithValue("$id", ""); file.Parameters.AddWithValue("$component", "");
                            file.Parameters.AddWithValue("$src", ""); file.Parameters.AddWithValue("$dst", "");
                            file.Parameters.AddWithValue("$hash", ""); file.Parameters.AddWithValue("$pkg", "");
                            for (var i = 0; i < ModCount; i++)
                            {
                                var modId = Guid.NewGuid();
                                var packageId = Guid.NewGuid();
                                mod.Parameters["$id"].Value = modId.ToString();
                                mod.Parameters["$name"].Value = $"{(i % 2 == 0 ? "Match" : "Other")} Perf Mod {i:D4}";
                                mod.Parameters["$author"].Value = $"Author{i % 50:D2}";
                                mod.ExecuteNonQuery();
                                pkg.Parameters["$id"].Value = packageId.ToString();
                                pkg.Parameters["$mod"].Value = modId.ToString();
                                pkg.Parameters["$name"].Value = $"{i:D4}.zip";
                                pkg.Parameters["$hash"].Value = $"{i:D8}";
                                pkg.ExecuteNonQuery();
                                for (var c = 0; c < 2; c++)
                                {
                                    var componentId = Guid.NewGuid();
                                    component.Parameters["$id"].Value = componentId.ToString();
                                    component.Parameters["$mod"].Value = modId.ToString();
                                    component.Parameters["$comp"].Value = $"comp::{i}:{c}";
                                    component.Parameters["$name"].Value = $"Component {i}:{c}";
                                    component.Parameters["$pkg"].Value = packageId.ToString();
                                    component.ExecuteNonQuery();
                                    for (var f = 0; f < 3; f++)
                                    {
                                        file.Parameters["$id"].Value = Guid.NewGuid().ToString();
                                        file.Parameters["$component"].Value = componentId.ToString();
                                        file.Parameters["$src"].Value = $"packages/{i}/{c}/{f}.patch_0";
                                        file.Parameters["$dst"].Value = $"data/{i}/{c}/{f}.patch_0";
                                        file.Parameters["$hash"].Value = $"{i:D4}{c}{f}";
                                        file.Parameters["$pkg"].Value = packageId.ToString();
                                        file.ExecuteNonQuery();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO DeploymentRecords (Id, SessionId, ProfileId, ModId, Workflow, DeploymentMethod, StartTime, EndTime, Status, OptionsSnapshotJson, DeploymentOrderIndex, ModVersion) VALUES ($id, $session, $profile, $mod, 'Default', 'Copy', $start, NULL, 'Completed', NULL, NULL, NULL)";
                record.Parameters.AddWithValue("$id", ""); record.Parameters.AddWithValue("$session", sessionId.ToString());
                record.Parameters.AddWithValue("$profile", sessionId.ToString()); record.Parameters.AddWithValue("$mod", "");
                record.Parameters.AddWithValue("$start", "");
                using (var dfile = connection.CreateCommand())
                {
                    dfile.Transaction = transaction;
                    dfile.CommandText = "INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath, Method) VALUES ($id, $record, $mod, $src, $dst, 'Copy')";
                    dfile.Parameters.AddWithValue("$id", ""); dfile.Parameters.AddWithValue("$record", "");
                    dfile.Parameters.AddWithValue("$mod", ""); dfile.Parameters.AddWithValue("$src", "");
                    dfile.Parameters.AddWithValue("$dst", "");
                    for (var i = 0; i < 200; i++)
                    {
                        var recordId = Guid.NewGuid();
                        using (var modRow = connection.CreateCommand())
                        {
                            var physicalModId = Guid.NewGuid();
                            modRow.Transaction = transaction;
                            modRow.CommandText = "INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES ($id, $name, '1.0.0', $path, $date, $date)";
                            modRow.Parameters.AddWithValue("$id", physicalModId.ToString());
                            modRow.Parameters.AddWithValue("$name", $"Deploy Mod {i:D3}");
                            modRow.Parameters.AddWithValue("$path", Path.Combine("mods", i.ToString("D3")));
                            modRow.Parameters.AddWithValue("$date", date);
                            modRow.ExecuteNonQuery();
                            record.Parameters["$mod"].Value = physicalModId.ToString();
                            dfile.Parameters["$mod"].Value = physicalModId.ToString();
                        }
                        record.Parameters["$id"].Value = recordId.ToString();
                        record.Parameters["$start"].Value = DateTime.UtcNow.AddSeconds(-200 + i).ToString("O");
                        record.ExecuteNonQuery();
                        for (var f = 0; f < 10; f++)
                        {
                            dfile.Parameters["$id"].Value = Guid.NewGuid().ToString();
                            dfile.Parameters["$record"].Value = recordId.ToString();
                            dfile.Parameters["$src"].Value = $"store/{i}/{f}.pak";
                            dfile.Parameters["$dst"].Value = $"data/{i}/{f}.pak";
                            dfile.ExecuteNonQuery();
                        }
                    }
                }
            }
            transaction.Commit();
        }
        return sessionId;
    }
}
