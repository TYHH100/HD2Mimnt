using System.Buffers.Binary;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ModCompatibilityAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntCompatibilityTests", Guid.NewGuid().ToString("N"));

    public ModCompatibilityAnalyzerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AnalyzeAsync_ValidUnitPatch_ReportsUnknownWithoutReference()
    {
        var patch = WritePatch("abcdeffedcba9876.patch_0", entryIndex: 1, declaredCount: 1, unitVersion: 0xA4CD36);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root);

        Assert.Equal(ModCompatibilityStatus.Unknown, report.Status);
        Assert.Single(report.Patches);
        Assert.Single(report.Units);
        Assert.Equal((uint)0xA4CD36, report.Units[0].Version);
        Assert.True(report.Patches[0].HeaderValid);
        Assert.True(report.Patches[0].IndexValid);
        Assert.True(File.Exists(patch));
    }

    [Fact]
    public async Task RepairAsync_RepairsEntryIndexAndTypeCount_WithBackup()
    {
        WritePatch("abcdeffedcba9876.patch_0", entryIndex: 7, declaredCount: 9, unitVersion: 1);
        var analyzer = new ModCompatibilityAnalyzer();

        var plan = await analyzer.CreateRepairPlanAsync(_root);
        Assert.True(plan.CanRepair);
        Assert.Equal(2, plan.ActionCount);

        var result = await analyzer.RepairAsync(_root);
        Assert.True(result.Success);
        Assert.Equal(2, result.AppliedActionCount);
        Assert.Single(result.BackupPaths);
        Assert.True(File.Exists(result.BackupPaths[0]));

        var report = await analyzer.AnalyzeAsync(_root, referenceVersion: 1);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.True(report.Patches[0].EntryIndicesValid);
        Assert.True(report.Patches[0].TypeDistributionValid);
        var backups = await analyzer.GetBackupsAsync(_root);
        Assert.Single(backups);
        var restored = await analyzer.RestoreBackupAsync(_root, backups[0]);
        Assert.True(restored.Success);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(
            Path.Combine(_root, "abcdeffedcba9876.patch_0")).AsSpan(180)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(136, ulong.MaxValue)]
    public async Task RepairAsync_InvalidPayloadRange_DoesNotChangeOriginal(int offset, ulong value)
    {
        var path = WritePatch("abcdeffedcba9876.patch_0", 7, 1, 1, gpuSize: 8);
        File.WriteAllBytes(path + ".gpu_resources", new byte[8]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        File.WriteAllBytes(path, bytes);
        var analyzer = new ModCompatibilityAnalyzer();
        var report = await analyzer.AnalyzeAsync(_root, 1);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.True(report.HasBlockingIssues);
        Assert.False((await analyzer.CreateRepairPlanAsync(_root)).CanRepair);
        Assert.False((await analyzer.RepairAsync(_root)).Success);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(await analyzer.GetBackupsAsync(_root));
    }

    [Fact]
    public async Task RepairAsync_CompanionsPresent_ValidatesUsingOriginalCompanionPaths()
    {
        var path = WritePatch("abcdeffedcba9876.patch_0", 7, 1, 1, gpuSize: 8);
        File.WriteAllBytes(path + ".gpu_resources", new byte[8]);
        var analyzer = new ModCompatibilityAnalyzer();
        Assert.True((await analyzer.RepairAsync(_root)).Success);
        Assert.Equal(ModCompatibilityStatus.Compatible, (await analyzer.AnalyzeAsync(_root, 1)).Status);
    }

    [Fact]
    public async Task RepairAsync_PreCanceled_DoesNotCreateFiles()
    {
        WritePatch("abcdeffedcba9876.patch_0", 7, 1, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var analyzer = new ModCompatibilityAnalyzer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.RepairAsync(_root, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(_root, cancellationToken: cancellation.Token));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AnalyzeAsync_NoUnitOrNoFiles_SkipsModelCheck()
    {
        var analyzer = new ModCompatibilityAnalyzer();
        Assert.Equal(ModCompatibilityStatus.NotApplicable, (await analyzer.AnalyzeAsync(_root, 1)).Status);
        var path = WritePatch("abcdeffedcba9876.patch_0", 1, 1, 1);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), 1);
        File.WriteAllBytes(path, bytes);
        var report = await analyzer.AnalyzeAsync(_root, 1);
        Assert.Equal(ModCompatibilityStatus.NotApplicable, report.Status);
        Assert.Equal(ModCompatibilityReason.NoModelResources, report.Reason);
    }

    [Theory]
    [InlineData(0xa14e8dfa2cd117e2UL)] // Lua
    [InlineData(0x57a13425279979d7UL)] // IK skeleton
    [InlineData(0x931e336d7646cc26UL)] // Animation
    [InlineData(0x535a7bd3e650d799UL)] // Wwise bank
    public async Task AnalyzeAsync_NonModelResource_SkipsVersionsAndModelDiagnostics(ulong type)
    {
        var path = CompatibilityFixture.WritePatch(_root, companions: false,
            resources: [new(1, type, new byte[0x80], Stream: new byte[8])]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(180), 7);
        File.WriteAllBytes(path, bytes);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root, 123);
        Assert.Equal(ModCompatibilityStatus.NotApplicable, report.Status);
        Assert.Empty(report.Units); Assert.Equal(0, report.ComparedUnitCount);
        Assert.False(report.HasBlockingIssues);
        Assert.False(Assert.Single(report.Patches).ContainsUnitResources);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyUnitTypeDeclaration_DoesNotTreatSkeletonAsUnreadableModel()
    {
        var path = CompatibilityFixture.WritePatch(_root,
            resources: [new(1, 0x18dead01056b72e9UL, new byte[16])]);
        var original = File.ReadAllBytes(path);
        var bytes = new byte[original.Length + 32];
        original.AsSpan(0, 104).CopyTo(bytes);
        original.AsSpan(104).CopyTo(bytes.AsSpan(136));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), CompatibilityFixture.UnitType);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(152), 216);
        File.WriteAllBytes(path, bytes);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root);
        Assert.Equal(ModCompatibilityStatus.NotApplicable, report.Status);
        Assert.Equal(ModCompatibilityReason.NoModelResources, report.Reason);
        Assert.False(Assert.Single(report.Patches).ContainsUnitResources);
        Assert.Empty(report.Units);
        Assert.False(report.HasBlockingIssues);
    }

    [Fact]
    public async Task AnalyzeAsync_ModelAlongsideNonModelPatches_DoesNotLoseMatchedModel()
    {
        CompatibilityFixture.WritePatch(_root, resources: [new(1, CompatibilityFixture.UnitType, CompatibilityFixture.Unit())]);
        CompatibilityFixture.WritePatch(_root, "1111111111111111.patch_0", companions: false,
            resources: [new(2, 0x535a7bd3e650d799UL, new byte[16], Stream: new byte[8])]);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root, CompatibilityFixture.CurrentVersion);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.Single(report.Units); Assert.Equal(1, report.ComparedUnitCount);
        Assert.Equal(2, report.Patches.Count);
        Assert.False(report.HasBlockingIssues);
    }

    [Fact]
    public async Task AnalyzeAsync_BrokenPatch_DoesNotLoseOtherFindings()
    {
        WritePatch("abcdeffedcba9876.patch_0", 1, 1, 1);
        File.WriteAllBytes(Path.Combine(_root, "abcdeffedcba9876.patch_1"), []);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root, 1);
        Assert.Equal(2, report.Patches.Count);
        Assert.Single(report.Units);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.True(report.HasBlockingIssues);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableModelVersion_ReportsReadReasonWithoutMismatch()
    {
        File.WriteAllBytes(Path.Combine(_root, "abcdeffedcba9876.patch_0"), []);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root, 1);
        Assert.Equal(ModCompatibilityStatus.Unknown, report.Status);
        Assert.Equal(ModCompatibilityReason.ModelDataUnreadable, report.Reason);
        Assert.True(report.HasBlockingIssues);
    }

    [Fact]
    public async Task RestoreBackupAsync_BackupOutsideMod_IsRejected()
    {
        var analyzer = new ModCompatibilityAnalyzer();
        await Assert.ThrowsAsync<InvalidDataException>(() => analyzer.RestoreBackupAsync(_root,
            Path.Combine(Path.GetDirectoryName(_root)!, "abcdeffedcba9876.patch_0.20261017-000000.hd2mimnt-backup")));
    }

    [Fact]
    public async Task RepairAsync_SecondFileLocked_RollsBackFirstAndCleansStaging()
    {
        var first = WritePatch("abcdeffedcba9876.patch_0", 7, 1, 1);
        var second = WritePatch("abcdeffedcba9876.patch_1", 8, 1, 1);
        var beforeFirst = File.ReadAllBytes(first);
        var beforeSecond = File.ReadAllBytes(second);
        using var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await new ModCompatibilityAnalyzer().RepairAsync(_root);
        Assert.False(result.Success);
        Assert.Empty(result.RecoveryErrors);
        Assert.Equal(beforeFirst, File.ReadAllBytes(first));
        Assert.Equal(beforeSecond, File.ReadAllBytes(second));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task CheckModsAsync_WithoutGameReference_KeepsAllUnknown()
    {
        WritePatch("abcdeffedcba9876.patch_0", 1, 1, 1);
        var mod = new Mod { Id = Guid.NewGuid(), Path = _root };
        var repository = new Mock<IModRepository>();
        repository.Setup(repo => repo.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mod });
        var mods = new ModService(repository.Object, Path.Combine(_root, "storage"));
        var service = new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(), mods);
        var reports = await service.CheckModsAsync([mod.Id, mod.Id]);
        Assert.Single(reports);
        Assert.Equal(ModCompatibilityStatus.Unknown, reports[0].Status);
    }

    [Fact]
    public async Task RepairAsync_Application_UpdatesContentCachesAndKeepsIdentity()
    {
        WritePatch("abcdeffedcba9876.patch_0", 7, 1, 1);
        var mod = new Mod { Id = Guid.NewGuid(), Path = _root, Hash = "old", ResourceTypeIds = [1] };
        var repository = new Mock<IModRepository>();
        repository.Setup(repo => repo.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        var mods = new ModService(repository.Object, Path.Combine(_root, "storage"));
        var notifications = 0;
        mods.ModsChanged += (_, _) => notifications++;
        var service = new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(), mods);
        Assert.True((await service.RepairAsync(mod.Id)).Success);
        Assert.Empty(mod.Hash);
        Assert.Null(mod.ResourceTypeIds);
        Assert.Equal(1, notifications);
        repository.Verify(repo => repo.UpdateAsync(mod, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_RequiredCompanionMissing_KeepsVersionResultAndDiagnostic()
    {
        WritePatch("abcdeffedcba9876.patch_0", entryIndex: 1, declaredCount: 1, unitVersion: 1, gpuSize: 8);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(_root, referenceVersion: 1);

        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.True(report.HasBlockingIssues);
        Assert.Contains(report.Issues, issue => issue.Contains("gpu_resources", StringComparison.OrdinalIgnoreCase));
    }

    private string WritePatch(string name, uint entryIndex, ulong declaredCount, uint unitVersion, uint gpuSize = 0)
    {
        var path = Path.Combine(_root, name);
        var bytes = new byte[72 + 32 + 80 + 0x80];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72 + 8), 0xe0a48d0be9a7453f);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72 + 16), declaredCount);
        var entry = bytes.AsSpan(104, 80);
        BinaryPrimitives.WriteUInt64LittleEndian(entry, 0x0102030405060708);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], 0xe0a48d0be9a7453f);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], 184);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[56..], 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[64..], gpuSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], entryIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(184 + 0x2C), unitVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(184 + 0x60), 0x78);
        if (gpuSize > 0 && unitVersion < 0xA4CD36)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(184 + 0x5c), 0x68);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
