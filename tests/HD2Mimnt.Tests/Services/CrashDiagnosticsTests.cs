using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Diagnostics;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class CrashDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-CrashTests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _time = new(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc);
    private string Game => Path.Combine(_root, "Helldivers 2");
    public CrashDiagnosticsTests() => Directory.CreateDirectory(_root);
    private CrashModSnapshot Mod(string name, int order = 0) => new(Guid.NewGuid(), name, "1.0", "hash", _time, "metadata", "", order);
    private CrashDeploymentSnapshot Snapshot(DateTime time, IReadOnlyList<CrashModSnapshot> mods, bool good = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Test", Game, time, time.AddSeconds(5), "Completed", good, mods);

    [Fact]
    public void JsonReader_ShouldHandleNullFieldsAndUseOnlyTheCrashingThread()
    {
        var result = MinidumpJsonReader.Read("""
            {"main_module":0,"modules":[{"filename":"helldivers2.exe","version":null}],
             "crash_info":{"type":"EXCEPTION_ACCESS_VIOLATION_READ","address":"0x12","crashing_thread":1},
             "crashing_thread":null,"threads":[{"frames":[{"module":"unrelated.dll"}]},
             {"frames":[{"frame":0,"module":"helldivers2.exe","trust":"context","function":null}]}]}
            """);
        Assert.Equal("helldivers2.exe", result.MainModule);
        Assert.Equal("helldivers2.exe", Assert.Single(result.Frames).Module);
        Assert.Equal(string.Empty, result.Frames[0].Function);
        Assert.Empty(MinidumpJsonReader.Read("{\"modules\":null,\"crash_info\":null,\"threads\":null}").Frames);
    }

    [Fact]
    public void Compare_ShouldIdentifyAddUpdateOptionsAndRemovalWithoutIndexShiftFalsePositives()
    {
        var a = Mod("A"); var b = Mod("B", 1); var c = Mod("C", 2); var removed = Mod("removed", 3);
        var added = Mod("new");
        var changes = CrashDeploymentHistoryService.Compare([a, b, c, removed],
            [added, a with { Order = 1, Version = "2.0" }, b with { Order = 2, OptionsJson = "changed" }, c with { Order = 3 }]);
        Assert.Contains(changes, change => change.ModId == added.ModId && change.Kinds.SequenceEqual([CrashModChangeKind.Added]));
        Assert.Contains(changes, change => change.ModId == a.ModId && change.Kinds.SequenceEqual([CrashModChangeKind.Updated]));
        Assert.Contains(changes, change => change.ModId == b.ModId && change.Kinds.SequenceEqual([CrashModChangeKind.OptionsChanged]));
        Assert.Contains(changes, change => change.ModId == removed.ModId && change.Kinds.SequenceEqual([CrashModChangeKind.Removed]));
        Assert.DoesNotContain(changes, change => change.ModId == c.ModId);
        Assert.All(changes, change => Assert.DoesNotContain(CrashModChangeKind.OrderChanged, change.Kinds));
    }

    [Fact]
    public void Compare_ShouldDetectRelativeReorderAndLocalMetadataChanges()
    {
        var a = Mod("A"); var b = Mod("B", 1);
        var changes = CrashDeploymentHistoryService.Compare([a, b], [b with { Order = 0 }, a with { Order = 1, FileMetadataHash = "changed" }]);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Contains(CrashModChangeKind.OrderChanged, change.Kinds));
        Assert.Contains(CrashModChangeKind.Updated, changes.Single(change => change.ModId == a.ModId).Kinds);
    }

    [Fact]
    public async Task CompareAsync_ShouldChooseTheDeploymentBeforeDumpAndPreferConfirmedWorkingBaseline()
    {
        var mod = Mod("A"); var good = Snapshot(_time, [mod], true);
        var previous = Snapshot(_time.AddMinutes(1), [mod with { Version = "2.0" }]);
        var crash = Snapshot(_time.AddMinutes(2), [mod with { Version = "3.0" }]);
        var future = Snapshot(_time.AddMinutes(4), [Mod("future")], true);
        var wrongGame = Snapshot(_time.AddMinutes(2.5), []) with { GamePath = Path.Combine(_root, "other") };
        var repo = new Mock<ICrashHistoryRepository>();
        repo.Setup(value => value.GetRecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync([future, wrongGame, crash, previous, good]);
        var result = await new CrashDeploymentHistoryService(repo.Object).CompareAsync(_time.AddMinutes(3), Game);
        Assert.Equal(crash.SessionId, result!.Deployment.SessionId);
        Assert.Equal(good.SessionId, result.Baseline!.SessionId);
        Assert.Equal(CrashModChangeKind.Updated, Assert.Single(Assert.Single(result.Changes).Kinds));
    }

    [Fact]
    public async Task CompareAsync_ShouldNotTreatDeploymentCompletedAfterCrashAsCompletedAtCrash()
    {
        var snapshot = Snapshot(_time, [Mod("A")]) with { EndedUtc = _time.AddMinutes(5) };
        var repo = new Mock<ICrashHistoryRepository>();
        repo.Setup(value => value.GetRecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync([snapshot]);
        var result = await new CrashDeploymentHistoryService(repo.Object).CompareAsync(_time.AddMinutes(1), Game);
        Assert.Equal("InProgressAtCrash", result!.Deployment.Status);
        Assert.Null(result.Baseline);
    }

    [Fact]
    public async Task RemovalHistory_ShouldRetainCumulativeRemovalsAndUseRealDeploymentAsFallbackBaseline()
    {
        var database = Path.Combine(_root, "test.db");
        using var context = new HD2MimntDbContext(database); await context.InitializeDatabaseAsync();
        var history = new CrashDeploymentHistoryService(new CrashHistoryRepository(database));
        var a = Mod("A"); var b = Mod("B", 1); var c = Mod("C", 2);
        var previous = Snapshot(_time, [a, b, c]); await history.SaveAsync(previous);
        await history.RecordRemovalAsync(previous.SessionId, [a.ModId]);
        await history.RecordRemovalAsync(previous.SessionId, [b.ModId]);
        var snapshots = await history.GetRecentAsync();
        Assert.Equal(c.ModId, Assert.Single(snapshots[0].Mods).ModId);
        var next = Snapshot(DateTime.UtcNow.AddSeconds(1), [c]); await history.SaveAsync(next);
        var comparison = await history.CompareAsync(DateTime.UtcNow.AddMinutes(1), Game);
        Assert.Equal(previous.SessionId, comparison!.Baseline!.SessionId);
        Assert.Equal(2, comparison.Changes.Count);
        Assert.All(comparison.Changes, change => Assert.Equal(CrashModChangeKind.Removed, Assert.Single(change.Kinds)));
    }

    [Fact]
    public async Task Export_ShouldWriteStructuredReportAndPreventOverwritingDump()
    {
        var dump = Path.Combine(_root, "source.dmp"); await File.WriteAllTextAsync(dump, "source");
        var report = new CrashDiagnosisReport(dump, _time, new("ACCESS", "0x1", "helldivers2.exe", [], []), true,
            CrashAssessment.Inconclusive, [], null, false, null);
        var service = new CrashDiagnosticsService(Mock.Of<IMinidumpAnalyzer>(), Mock.Of<IDeploymentRepository>(),
            Mock.Of<IModRepository>(), new CrashDeploymentHistoryService(Mock.Of<ICrashHistoryRepository>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(report, dump));
        Assert.Equal("source", await File.ReadAllTextAsync(dump));
        var output = Path.Combine(_root, "report.json"); await service.ExportAsync(report, output);
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal("Inconclusive", json.RootElement.GetProperty("Assessment").GetString());
        Assert.Contains('\n', await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData(false, false, "context", true)]
    [InlineData(true, false, "context", false)]
    [InlineData(false, false, "scan", false)]
    [InlineData(false, true, "context", false)]
    public async Task Analyze_ShouldKeepDuplicateNamesScanFramesAndFutureDeploymentsInconclusive(bool duplicate, bool future, string trust, bool clue)
    {
        Directory.CreateDirectory(Game); var target = Path.Combine(Game, "dxgi.dll"); await File.WriteAllTextAsync(target, "sample");
        var id = Guid.NewGuid();
        var modules = new List<CrashModule> { new("helldivers2.exe", "", "", ""), new("dxgi.dll", "", "0x10", "0x20") };
        if (duplicate) modules.Add(new("dxgi.dll", "", "0x30", "0x40"));
        var analyzer = new Mock<IMinidumpAnalyzer>();
        analyzer.Setup(value => value.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new CrashDumpAnalysis("ACCESS", "0x1", "helldivers2.exe", [new(0, "dxgi.dll", "0x1", "", trust)], modules, _time));
        var deployments = new Mock<IDeploymentRepository>();
        deployments.Setup(value => value.GetLatestDeploymentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new DeploymentRecord { ModId = id, StartTime = future ? _time.AddDays(1) : _time.AddDays(-1), Status = "Completed",
                Files = [new DeploymentFile { ModId = id, TargetPath = target }] }]);
        var mods = new Mock<IModRepository>();
        mods.Setup(value => value.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync([new Mod { Id = id, Name = "test" }]);
        var history = new Mock<ICrashHistoryRepository>();
        history.Setup(value => value.GetRecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var report = await new CrashDiagnosticsService(analyzer.Object, deployments.Object, mods.Object,
            new CrashDeploymentHistoryService(history.Object)).AnalyzeAsync(Path.Combine(_root, "test.dmp"), Game);
        Assert.Equal(clue ? CrashAssessment.ModClues : CrashAssessment.Inconclusive, report.Assessment);
        Assert.Equal(future, report.DeploymentAfterDump);
    }

    [Fact]
    public async Task History_ShouldEnforceTotalLimitEvenWhenAllSnapshotsAreMarkedGood()
    {
        var database = Path.Combine(_root, "test.db");
        using var context = new HD2MimntDbContext(database); await context.InitializeDatabaseAsync();
        var repository = new CrashHistoryRepository(database);
        var good = Snapshot(_time, [Mod("baseline")]); await repository.SaveAsync(good); await repository.MarkKnownGoodAsync(good.SessionId);
        for (var index = 1; index < 54; index++)
        {
            var snapshot = Snapshot(_time.AddMinutes(index), []);
            await repository.SaveAsync(snapshot);
            await repository.MarkKnownGoodAsync(snapshot.SessionId);
            Assert.True((await repository.GetRecentAsync()).Count <= 50);
        }
        var reopened = new CrashHistoryRepository(database);
        var recent = await reopened.GetRecentAsync();
        Assert.Equal(50, recent.Count);
        Assert.All(recent, value => Assert.True(value.KnownGood));
        Assert.DoesNotContain(recent, value => value.SessionId == good.SessionId);
        Assert.Equal(_time.AddMinutes(4), recent.Last().StartedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkKnownGoodAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeployAndCleanup_ShouldRecordRealSelectedFilesAndPreserveHistory()
    {
        Directory.CreateDirectory(Game); var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "aaaaaaaaaaaaaaaa.patch_0"), "sample patch");
        var database = Path.Combine(_root, "test.db");
        using var context = new HD2MimntDbContext(database); await context.InitializeDatabaseAsync();
        var mods = new ModRepository(context); var profiles = new ProfileRepository(context); var deployments = new DeploymentRepository(context);
        var history = new CrashDeploymentHistoryService(new CrashHistoryRepository(database));
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Test", Version = "1.0", Path = source, ImportedAt = _time, ModifiedAt = _time,
            BslRole = BslModRole.None, BslDetectionVersion = BslModDetector.DetectionVersion };
        await mods.AddAsync(mod);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Test", CreatedAt = _time, ModifiedAt = _time,
            Mods = [new ProfileMod { ModId = mod.Id, Enabled = true }] };
        await profiles.AddAsync(profile);
        var service = new DeploymentService(profiles, mods, deployments, [new CopyDeploymentMethod()], crashHistory: history);
        var result = await service.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, Game);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var snapshot = Assert.Single(await history.GetRecentAsync());
        Assert.Equal(result.SessionId, snapshot.SessionId);
        Assert.Equal(mod.Id, Assert.Single(snapshot.Mods).ModId);
        Assert.Equal(64, snapshot.Mods[0].FileMetadataHash.Length);
        Assert.Equal("Completed", snapshot.Status);
        await service.PreDeploymentCleanupAsync();
        Assert.Empty(await deployments.GetLatestDeploymentsAsync());
        var retained = await history.GetRecentAsync();
        Assert.Equal(2, retained.Count);
        Assert.Empty(retained[0].Mods);
        Assert.Single(retained[1].Mods);
    }

    [Fact]
    public async Task Analyzer_ShouldRejectCorruptDumpAndCleanTemporaryOutput()
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "Tools", "minidump-stackwalk", "minidump-stackwalk.exe");
        Assert.True(File.Exists(tool));
        var temp = Path.Combine(_root, "temp");
        var dump = Path.Combine(_root, "bad dump.dmp");
        await File.WriteAllBytesAsync(dump, "MDMP"u8.ToArray().Concat(new byte[28]).ToArray());
        var analyzer = new MinidumpStackwalkAnalyzer(tool, temp);
        await Assert.ThrowsAsync<InvalidDataException>(() => analyzer.AnalyzeAsync(dump));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temp, "crash-diagnostics")));
        Assert.Equal(32, new FileInfo(dump).Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(dump, cancelled.Token));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
