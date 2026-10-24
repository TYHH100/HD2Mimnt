using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using Moq;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.Tests.Services;

public sealed class CompatibilityApplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntCompatibilityAppTests", Guid.NewGuid().ToString("N"));
    private sealed class SynchronousProgress(Action<CompatibilityBatchProgress> report) : IProgress<CompatibilityBatchProgress>
    { public void Report(CompatibilityBatchProgress value) => report(value); }

    private (Mock<IModRepository> Repository, ModService Mods, ModCompatibilityService Service) Service(IModCompatibilityAnalyzer analyzer, params Mod[] mods)
    {
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        foreach (var mod in mods) repository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        var service = new ModService(repository.Object, Path.Combine(_root, "storage"));
        return (repository, service, new ModCompatibilityService(repository.Object, analyzer, service));
    }

    [Fact]
    public async Task BatchPlan_DeduplicatesAndReportsMissingAndFailure_ContinuesInOrder()
    {
        var first = new Mod { Id = Guid.NewGuid(), Path = "bad", Name = "bad" };
        var second = new Mod { Id = Guid.NewGuid(), Path = "good", Name = "good" };
        var missing = Guid.NewGuid();
        var analyzer = new Mock<IModCompatibilityAnalyzer>();
        analyzer.Setup(a => a.CreateFullRepairPlanAsync(first.Path, null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));
        var plan = new CompatibilityFullRepairPlan();
        plan.Actions.Add(new() { Kind = "UnitMigration" });
        analyzer.Setup(a => a.CreateFullRepairPlanAsync(second.Path, null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);
        var (repository, _, service) = Service(analyzer.Object, first, second);
        var progress = new List<CompatibilityBatchProgress>();
        var items = await service.CreateBatchRepairPlanAsync([missing, first.Id, first.Id, second.Id], progress: new SynchronousProgress(progress.Add));
        Assert.Equal(new[] { missing, first.Id, second.Id }, items.Select(i => i.ModId));
        Assert.Equal(new[] { CompatibilityBatchState.Failed, CompatibilityBatchState.Failed, CompatibilityBatchState.Repairable }, items.Select(i => i.State));
        Assert.Equal(new[] { 1, 2, 3 }, progress.Select(p => p.Completed));
        Assert.All(progress, p => Assert.Equal(3, p.Total));
        repository.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        analyzer.Verify(a => a.CreateFullRepairPlanAsync(first.Path, null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RepairFull_InvalidatesCachesAndKeepsModAndProfileIdentity()
    {
        var path = Path.Combine(_root, "mod");
        WritePatch(path, resources: [new(1, MaterialType, Material())]);
        var mod = new Mod { Id = Guid.NewGuid(), Path = path, Hash = "old", ResourceTypeIds = [1], BslDetectionVersion = 9 };
        var profileMod = new ProfileMod { ModId = mod.Id, ProfileId = Guid.NewGuid() };
        var (repository, mods, service) = Service(new ModCompatibilityAnalyzer(), mod);
        var notifications = 0;
        mods.ModsChanged += (_, _) => notifications++;
        var result = await service.RepairFullAsync(mod.Id, new() { RepairUnits = false });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Changed);
        Assert.Equal(profileMod.ModId, mod.Id);
        Assert.Empty(mod.Hash);
        Assert.Null(mod.ResourceTypeIds);
        Assert.Equal(0, mod.BslDetectionVersion);
        Assert.Equal(1, notifications);
        repository.Verify(r => r.UpdateAsync(mod, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task RepairBatch_CanceledAfterCompletedMod_KeepsCompletedItemAndDoesNotStartNext()
    {
        var firstPath = Path.Combine(_root, "first");
        var secondPath = Path.Combine(_root, "second");
        WritePatch(firstPath, resources: [new(1, MaterialType, Material())]);
        var secondFile = WritePatch(secondPath, resources: [new(1, MaterialType, Material())]);
        var originalSecond = File.ReadAllBytes(secondFile);
        var first = new Mod { Id = Guid.NewGuid(), Path = firstPath };
        var second = new Mod { Id = Guid.NewGuid(), Path = secondPath };
        var (repository, _, service) = Service(new ModCompatibilityAnalyzer(), first, second);
        using var canceled = new CancellationTokenSource();
        var progress = new List<CompatibilityBatchProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RepairBatchAsync([first.Id, second.Id], new() { RepairUnits = false },
            new SynchronousProgress(p => { progress.Add(p); canceled.Cancel(); }), canceled.Token));
        Assert.Equal(CompatibilityBatchState.Repaired, Assert.Single(progress).Item.State);
        Assert.Equal(originalSecond, File.ReadAllBytes(secondFile));
        repository.Verify(r => r.UpdateAsync(first, CancellationToken.None), Times.Once);
        repository.Verify(r => r.UpdateAsync(second, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RepairBatch_OneFailedResult_ProcessesRemainingMod()
    {
        var first = new Mod { Id = Guid.NewGuid(), Path = "bad" };
        var second = new Mod { Id = Guid.NewGuid(), Path = "good" };
        var analyzer = new Mock<IModCompatibilityAnalyzer>();
        analyzer.Setup(a => a.RepairFullAsync(first.Path, null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompatibilityRepairResult { ErrorMessage = "no reference" });
        analyzer.Setup(a => a.RepairFullAsync(second.Path, null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompatibilityRepairResult { Success = true });
        var (_, _, service) = Service(analyzer.Object, first, second);
        var results = await service.RepairBatchAsync([first.Id, second.Id]);
        Assert.Equal(new[] { CompatibilityBatchState.Failed, CompatibilityBatchState.NoAction }, results.Select(i => i.State));
    }

    [Fact]
    public async Task BatchPlanAndRepair_UsePerModMixedLodChoicesWithoutCrossModUnion()
    {
        var a = new Mod { Id = Guid.NewGuid(), Path = "a" };
        var b = new Mod { Id = Guid.NewGuid(), Path = "b" };
        var analyzer = new Mock<IModCompatibilityAnalyzer>();
        var seen = new List<(string Path, CompatibilityRepairOptions Options)>();
        analyzer.Setup(x => x.CreateFullRepairPlanAsync(It.IsAny<string>(), null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, IReadOnlyList<string>, CompatibilityRepairOptions, CancellationToken>((path, _, _, options, _) => seen.Add((path, options)))
            .ReturnsAsync(new CompatibilityFullRepairPlan());
        analyzer.Setup(x => x.RepairFullAsync(It.IsAny<string>(), null, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CompatibilityRepairOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, IReadOnlyList<string>, CompatibilityRepairOptions, CancellationToken>((path, _, _, options, _) => seen.Add((path, options)))
            .ReturnsAsync(new CompatibilityRepairResult { Success = true });
        var (_, _, service) = Service(analyzer.Object, a, b);
        var options = new Dictionary<Guid, CompatibilityRepairOptions>
        {
            [a.Id] = new() { LodStrategy = CompatibilityLodStrategy.Mixed, PreserveModLodUnitIds = new HashSet<ulong> { 1 } },
            [b.Id] = new() { LodStrategy = CompatibilityLodStrategy.Mixed }
        };
        await service.CreateBatchRepairPlanAsync([a.Id, b.Id], perModOptions: options);
        await service.RepairBatchAsync([a.Id, b.Id], perModOptions: options);
        Assert.Equal(4, seen.Count);
        Assert.All(seen.Where(x => x.Path == "a"), x => Assert.Contains(1UL, x.Options.PreserveModLodUnitIds));
        Assert.All(seen.Where(x => x.Path == "b"), x => Assert.Empty(x.Options.PreserveModLodUnitIds));
    }

    [Fact]
    public async Task EnsureHash_PersistsDerivedCacheWithoutPublishingContentChange()
    {
        var mod = new Mod { Id = Guid.NewGuid(), Path = Path.Combine(_root, "hash"), Hash = "" };
        Directory.CreateDirectory(mod.Path); File.WriteAllText(Path.Combine(mod.Path, "payload"), "hash me");
        var (repository, mods, _) = Service(new ModCompatibilityAnalyzer(), mod);
        var changed = 0; mods.ModsChanged += (_, _) => changed++;
        var hash = await mods.EnsureModHashAsync(mod.Id);
        Assert.NotEmpty(hash); Assert.Equal(hash, mod.Hash); Assert.Equal(0, changed);
        repository.Verify(r => r.UpdateAsync(mod, It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
