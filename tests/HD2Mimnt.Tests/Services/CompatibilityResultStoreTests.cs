using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.Tests.Services;

public sealed class CompatibilityResultStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntCompatStoreTests", Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _dbContext;
    private readonly CompatibilityResultRepository _store;

    public CompatibilityResultStoreTests()
    {
        Directory.CreateDirectory(_root);
        _dbContext = new HD2MimntDbContext(Path.Combine(_root, "test.db"));
        _dbContext.InitializeDatabaseAsync().Wait();
        _store = new CompatibilityResultRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ModContentStamp_ChangesWhenDirectoryContentChanges()
    {
        var dir = Path.Combine(_root, "stamp");
        Directory.CreateDirectory(dir);
        Assert.Equal(string.Empty, ModContentStamp.Calculate(Path.Combine(dir, "missing")));
        var before = ModContentStamp.Calculate(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        Assert.NotEqual(before, ModContentStamp.Calculate(dir));
    }

    [Fact]
    public async Task Upsert_RoundTripsSummary_AndUpdatesExistingRow()
    {
        var modId = Guid.NewGuid();
        await InsertModsAsync((modId, "mod-a"));
        await _store.UpsertAsync([Sample(modId, ModCompatibilityStatus.Compatible, issues: ["issue-1", "issue-2"])]);

        var row = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(modId, row.ModId);
        Assert.Equal(ModCompatibilityStatus.Compatible, row.Status);
        Assert.Equal(ModCompatibilityReason.None, row.Reason);
        Assert.Equal(3, row.UnitCount);
        Assert.Equal(2, row.ComparedUnitCount);
        Assert.False(row.HasBlockingIssues);
        Assert.Equal("reference-test", row.ReferenceSource);
        Assert.Equal(["issue-1", "issue-2"], row.Issues);
        Assert.Equal("v1:1:2:3", row.ContentStamp);
        Assert.NotEqual(default, row.CheckedAtUtc);

        await _store.UpsertAsync([Sample(modId, ModCompatibilityStatus.Incompatible, stamp: "v1:9:9:9")]);
        var updated = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(ModCompatibilityStatus.Incompatible, updated.Status);
        Assert.True(updated.HasBlockingIssues);
        Assert.Equal("v1:9:9:9", updated.ContentStamp);
    }

    [Fact]
    public async Task GetAll_PrunesRowsForDeletedMods()
    {
        var kept = Guid.NewGuid();
        await InsertModsAsync((kept, "kept"));
        await _store.UpsertAsync([Sample(kept), Sample(Guid.NewGuid())]);
        Assert.Equal(kept, Assert.Single(await _store.GetAllAsync()).ModId);
    }

    [Fact]
    public async Task CheckMods_PersistsSummary_RestoreReturnsMatchingStampOnly()
    {
        var modDir = Path.Combine(_root, "mod");
        WritePatch(modDir, resources: [new(1, UnitType, Unit())]);
        var mod = new Mod { Id = Guid.NewGuid(), Path = modDir, Name = "mod" };
        await InsertModsAsync((mod.Id, "mod"));
        var service = BuildService(mod);

        var report = Assert.Single(await service.CheckModsAsync([mod.Id]));
        var row = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(mod.Id, row.ModId);
        Assert.Equal(report.Status, row.Status);
        Assert.Equal(report.UnitCount, row.UnitCount);
        Assert.Equal(ModContentStamp.Calculate(modDir), row.ContentStamp);

        var restored = await service.RestorePersistedResultsAsync();
        Assert.True(restored.TryGetValue(mod.Id, out var restoredReport));
        Assert.Equal(report.Status, restoredReport.Status);
        Assert.Equal(report.UnitCount, restoredReport.UnitCount);
        Assert.Equal(report.Reason, restoredReport.Reason);

        // 模组内容变化后内容戳失效，不再回填。
        await File.AppendAllTextAsync(Path.Combine(modDir, "abcdeffedcba9876.patch_0"), "x");
        Assert.Empty(await service.RestorePersistedResultsAsync());
    }

    [Fact]
    public async Task Restore_SkipsAndPrunesModsMissingFromRepository()
    {
        var modDir = Path.Combine(_root, "gone");
        WritePatch(modDir, resources: [new(1, UnitType, Unit())]);
        var modId = Guid.NewGuid();
        await _store.UpsertAsync([new PersistedCompatibilityResult
        {
            ModId = modId, Status = ModCompatibilityStatus.Compatible, Reason = ModCompatibilityReason.None,
            CheckedAtUtc = DateTime.UtcNow, ContentStamp = ModContentStamp.Calculate(modDir)
        }]);

        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Mod>());
        var service = new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(),
            new ModService(repository.Object, Path.Combine(_root, "storage")), resultStore: _store);

        Assert.Empty(await service.RestorePersistedResultsAsync());
        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public async Task Restore_CarriesPersistedBlockingIssueFlag()
    {
        var modDir = Path.Combine(_root, "flagged");
        WritePatch(modDir, resources: [new(1, UnitType, Unit())]);
        var modId = Guid.NewGuid();
        await InsertModsAsync((modId, "flagged"));
        await _store.UpsertAsync([new PersistedCompatibilityResult
        {
            ModId = modId, Status = ModCompatibilityStatus.Compatible, Reason = ModCompatibilityReason.None,
            HasBlockingIssues = true, CheckedAtUtc = DateTime.UtcNow, ContentStamp = ModContentStamp.Calculate(modDir)
        }]);

        var service = BuildService(new Mod { Id = modId, Path = modDir, Name = "flagged" });
        var restored = await service.RestorePersistedResultsAsync();
        Assert.True(restored[modId].HasBlockingStructureIssues);
    }

    private ModCompatibilityService BuildService(params Mod[] mods)
    {
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        return new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(),
            new ModService(repository.Object, Path.Combine(_root, "storage")), resultStore: _store);
    }

    private static PersistedCompatibilityResult Sample(Guid modId, ModCompatibilityStatus status = ModCompatibilityStatus.Unknown,
        string stamp = "v1:1:2:3", string[]? issues = null)
        => new()
        {
            ModId = modId,
            Status = status,
            Reason = ModCompatibilityReason.None,
            UnitCount = 3,
            ComparedUnitCount = 2,
            HasBlockingIssues = status == ModCompatibilityStatus.Incompatible,
            ReferenceSource = "reference-test",
            Issues = issues ?? [],
            CheckedAtUtc = DateTime.UtcNow,
            ContentStamp = stamp
        };

    private async Task InsertModsAsync(params (Guid Id, string Name)[] mods)
    {
        using var connection = await _dbContext.OpenConnectionAsync();
        foreach (var (id, name) in mods)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES ($id, $name, '1.0.0', $path, $now, $now)";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$path", Path.Combine(_root, name));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
    }
}
