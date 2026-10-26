using System.Buffers.Binary;
using System.Diagnostics;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit.Abstractions;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.Tests.Services;

public sealed class CompatibilityBackendTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntBackendTests", Guid.NewGuid().ToString("N"));
    private string ModPath => Path.Combine(_root, "mod");
    private string GamePath => Path.Combine(_root, "game");
    private static CompatibilityRepairOptions MetadataOnly => new() { RepairUnits = false, MigrateMaterials = false, RecoverCompanions = false };

    [Theory]
    [InlineData(false, false, false, ModCompatibilityStatus.Compatible)]
    [InlineData(true, false, false, ModCompatibilityStatus.Compatible)]
    [InlineData(false, true, false, ModCompatibilityStatus.Compatible)]
    [InlineData(false, false, true, ModCompatibilityStatus.Compatible)]
    public async Task AnalyzeAsync_GpuStructureAndSamples_KeepsWarningsSeparateFromVersionResult(bool index, bool position, bool unknown, ModCompatibilityStatus expected)
    {
        WritePatch(ModPath, resources: [GpuUnit(index, position, unknown)]);
        var report = await new ModCompatibilityAnalyzer().AnalyzeAsync(ModPath, CurrentVersion);
        Assert.Equal(expected, report.Status);
        Assert.True(report.Units[0].Analysis!.GpuChecked);
        if (unknown) Assert.NotEmpty(report.Units[0].Analysis!.Warnings);
        if (index || position) Assert.NotEmpty(report.Units[0].Analysis!.Errors);
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(104UL)]
    public async Task MetadataRepair_UniquePhysicalGap_RecoversMainOffset(ulong badOffset)
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit())]);
        var original = File.ReadAllBytes(path);
        var bad = original.ToArray();
        Put64(bad, 120, badOffset);
        File.WriteAllBytes(path, bad);
        var analyzer = new ModCompatibilityAnalyzer();
        var plan = await analyzer.CreateRepairPlanAsync(ModPath);
        Assert.True(plan.CanRepair);
        Assert.Contains(plan.Actions, a => a.Kind == CompatibilityRepairKind.MainDataOffset);
        Assert.True((await analyzer.RepairAsync(ModPath)).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(-16)]
    [InlineData(16)]
    public async Task MetadataRepair_InternalSizeAndPhysicalBoundary_RepairsUnitSize(int delta)
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit()), new(2, 1, new byte[32])]);
        var original = File.ReadAllBytes(path);
        var bad = original.ToArray();
        var table = 72 + 64;
        Put32(bad, table + 56, (uint)(Unit().Length + delta));
        File.WriteAllBytes(path, bad);
        var analyzer = new ModCompatibilityAnalyzer();
        var plan = await analyzer.CreateRepairPlanAsync(ModPath);
        Assert.True(plan.CanRepair);
        Assert.Contains(plan.Actions, a => a.Kind == CompatibilityRepairKind.UnitDataSize);
        Assert.True((await analyzer.RepairAsync(ModPath)).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task MetadataRepair_EqualSizedGaps_RejectsAmbiguousOffset()
    {
        var path = WritePatch(ModPath, resources: [new(1, 1, new byte[32]), new(2, 1, new byte[32]), new(3, 1, new byte[32])]);
        var bytes = File.ReadAllBytes(path);
        Put64(bytes, 104 + 16, ulong.MaxValue);
        Put64(bytes, 104 + 160 + 16, ulong.MaxValue);
        File.WriteAllBytes(path, bytes);
        Assert.False((await new ModCompatibilityAnalyzer().CreateRepairPlanAsync(ModPath)).CanRepair);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task MetadataRepair_TypeIdsAndUniqueAlignment_RebuildsFromContiguousGroups()
    {
        var path = WritePatch(ModPath, resources: [new(1, 1, new byte[16]), new(2, 2, new byte[16])]);
        var bytes = File.ReadAllBytes(path);
        Put64(bytes, 80, 2);
        Put32(bytes, 72 + 24, 0);
        File.WriteAllBytes(path, bytes);
        var analyzer = new ModCompatibilityAnalyzer();
        Assert.True((await analyzer.RepairAsync(ModPath)).Success);
        var repaired = File.ReadAllBytes(path);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(repaired.AsSpan(80)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(96)));
    }

    [Theory]
    [InlineData(CompatibilityLodStrategy.UseGameReference, 9)]
    [InlineData(CompatibilityLodStrategy.PreserveMod, 3)]
    public async Task FullRepair_LodStrategy_UpdatesOffsetsAndPreservesOtherPayloads(CompatibilityLodStrategy strategy, byte expectedLod)
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(1)), new(2, 1, Enumerable.Repeat((byte)77, 32).ToArray())]);
        var original = File.ReadAllBytes(path);
        WriteGame(GamePath, new Resource(1, UnitType, Unit(CurrentVersion, 32, 9)));
        var analyzer = new ModCompatibilityAnalyzer();
        var options = new CompatibilityRepairOptions { LodStrategy = strategy };
        var plan = await analyzer.CreateFullRepairPlanAsync(ModPath, GamePath, [], options);
        Assert.True(plan.CanRepair, string.Join(";", plan.BlockingReasons));
        Assert.Equal(original, File.ReadAllBytes(path));
        var result = await analyzer.RepairFullAsync(ModPath, GamePath, [], options);
        Assert.True(result.Success, result.ErrorMessage);
        var report = await analyzer.AnalyzeAsync(ModPath, CurrentVersion);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        var bytes = File.ReadAllBytes(path);
        var firstOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(136 + 16));
        Assert.Equal(expectedLod, bytes[firstOffset + 0x78]);
        var secondOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(136 + 80 + 16));
        Assert.All(bytes.AsSpan(secondOffset, 32).ToArray(), value => Assert.Equal(77, value));
        Assert.Single(result.BackupPaths);
        Assert.True((await analyzer.RestoreBackupAsync(ModPath, result.BackupPaths[0])).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task FullRepair_LegacyLayout_ConvertsFormatsAndValidatesGpu()
    {
        var legacy = GpuUnit();
        Put32(legacy.Data, 0x2c, 1);
        Put32(legacy.Data, 0x98 + 12, 29);
        Put32(legacy.Data, 0x98 + 0x164, 4);
        Put32(legacy.Data, 0x98 + 0x1a4, 4);
        Put32(legacy.Data, 0x98 + 0x1a8, 4);
        legacy = legacy with { Gpu = new byte[10] };
        var path = WritePatch(ModPath, resources: [legacy]);
        var current = legacy.Data.ToArray();
        Put32(current, 0x2c, CurrentVersion);
        Put32(current, 0x98 + 12, 33);
        WriteGame(GamePath, legacy with { Data = current });
        var analyzer = new ModCompatibilityAnalyzer();
        var result = await analyzer.RepairFullAsync(ModPath, GamePath, [], new() { LodStrategy = CompatibilityLodStrategy.PreserveMod });
        Assert.True(result.Success, result.ErrorMessage);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(33u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(184 + 0x98 + 12)));
        Assert.Equal(ModCompatibilityStatus.Compatible, (await analyzer.AnalyzeAsync(ModPath, CurrentVersion)).Status);
    }

    [Fact]
    public async Task FullRepair_LegacyUnitWithUnnormalizedWeights_SamplingErrorsDoNotBlock()
    {
        // 旧版 Unit 的 GPU 载荷自带首顶点权重不归一（0.2x4）：迁移到当前版本后深度检验
        // 会报"GPU 采样权重总和无效"。这是作者网格数据特征而非迁移损坏（GPU 载荷未被修改），
        // 按旧管理器行为只作警告，不阻断修复。
        var legacy = GpuUnit(unnormalizedWeights: true);
        Put32(legacy.Data, 0x2c, 1);
        Put32(legacy.Data, 0x98 + 12, 31); // 旧版 Layout itemFormat 需 +4，迁移后为 35（half4 权重分量）
        var path = WritePatch(ModPath, resources: [legacy]);
        var current = legacy.Data.ToArray();
        Put32(current, 0x2c, CurrentVersion);
        Put32(current, 0x98 + 12, 35);
        WriteGame(GamePath, legacy with { Data = current });
        var analyzer = new ModCompatibilityAnalyzer();

        var result = await analyzer.RepairFullAsync(ModPath, GamePath, [], new() { LodStrategy = CompatibilityLodStrategy.PreserveMod });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Changed);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(CurrentVersion, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(184 + 0x2c)));
        Assert.Equal(35u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(184 + 0x98 + 12)));

        // 修复后的检查仍保留采样诊断（Health=Warning），但版本结论不受影响。
        var report = await analyzer.AnalyzeAsync(ModPath, CurrentVersion);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.Contains(report.Units[0].Analysis!.Errors, error => error.StartsWith("GPU 采样", StringComparison.Ordinal));
        Assert.True(report.Units[0].Analysis!.HasStructuralErrors == false);
    }

    [Theory]
    [InlineData(false, 320, 0x8F669F365F24594EUL)]
    [InlineData(true, 480, 0xC6042E3403385D40UL)]
    public async Task FullRepair_KnownMaterialSchema_MigratesParentOrEmissive(bool emissive, int size, ulong parent)
    {
        var path = WritePatch(ModPath, resources: [new(1, MaterialType, Material(emissive)), new(2, 1, new byte[16])]);
        var analyzer = new ModCompatibilityAnalyzer();
        var options = new CompatibilityRepairOptions { RepairUnits = false };
        var result = await analyzer.RepairFullAsync(ModPath, null, [], options);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Changed);
        var data = File.ReadAllBytes(path);
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(136 + 56)));
        Assert.Equal(parent, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(296 + 0x18)));
    }

    [Fact]
    public async Task FullRepair_UnrecognizedMaterialSemantics_DoesNotRewrite()
    {
        var material = Material();
        Put32(material, 0x88, 42);
        var path = WritePatch(ModPath, resources: [new(1, MaterialType, material)]);
        var original = File.ReadAllBytes(path);
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, null, [], new() { RepairUnits = false });
        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRepair_MissingCompanions_RecoversExactCopyOrGameSegments(bool useGame)
    {
        var resource = new Resource(1, 1, [1, 2, 3, 4], [8, 9, 10], [6, 7]);
        var path = WritePatch(ModPath, companions: false, resources: [resource]);
        var source = Path.Combine(_root, "source");
        if (useGame) WriteGame(GamePath, resource);
        else WritePatch(source, resources: [resource]);
        var analyzer = new ModCompatibilityAnalyzer();
        var result = await analyzer.RepairFullAsync(ModPath, useGame ? GamePath : null, [source], new() { RepairUnits = false });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(resource.Gpu, File.ReadAllBytes(path + ".gpu_resources"));
        Assert.Equal(resource.Stream, File.ReadAllBytes(path + ".stream"));
        Assert.Equal(2, result.RecoveredPaths.Count);
        Assert.True((await analyzer.RestoreBackupAsync(ModPath, result.BackupPaths.First(b => b.Contains(".gpu_resources.")))).Success);
        Assert.False(File.Exists(path + ".gpu_resources"));
        Assert.True(File.Exists(path + ".stream"));
    }

    [Fact]
    public async Task FullRepair_ModifiedMainResource_RejectsGameCompanion()
    {
        WritePatch(ModPath, companions: false, resources: [new(1, 1, [1, 2], [9])]);
        WriteGame(GamePath, new Resource(1, 1, [1, 3], [9]));
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, GamePath, [], new() { RepairUnits = false });
        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(ModPath, "*.gpu_resources"));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Fact]
    public async Task FullRepair_SecondPublicationLocked_RollsBackAllChanges()
    {
        var first = WritePatch(ModPath, resources: [new(1, UnitType, Unit(1))]);
        var second = WritePatch(ModPath, "abcdeffedcba9876.patch_1", resources: [new(1, UnitType, Unit(1))]);
        var original = File.ReadAllBytes(first);
        WriteGame(GamePath, new Resource(1, UnitType, Unit()));
        using var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, GamePath, [], new());
        Assert.False(result.Success);
        Assert.Empty(result.RecoveryErrors);
        Assert.Equal(original, File.ReadAllBytes(first));
        Assert.Equal(original, File.ReadAllBytes(second));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Fact]
    public async Task BackupHistory_IntegrityCleanupAndUtcRollback_WorkTogether()
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(1))]);
        var original = File.ReadAllBytes(path);
        WriteGame(GamePath, new Resource(1, UnitType, Unit()));
        var analyzer = new ModCompatibilityAnalyzer();
        Assert.True((await analyzer.RepairFullAsync(ModPath, GamePath, [], new())).Success);
        var first = Assert.Single(await analyzer.GetBackupHistoryAsync(ModPath));
        Assert.True(first.CanRestore);
        Assert.True((await analyzer.RollbackToAsync(ModPath, first.CreatedAtUtc)).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1, await analyzer.CleanOldBackupsAsync(ModPath, 1));
        var remaining = Assert.Single(await analyzer.GetBackupHistoryAsync(ModPath));
        var bytes = File.ReadAllBytes(remaining.BackupPath);
        bytes[0] ^= 1;
        File.WriteAllBytes(remaining.BackupPath, bytes);
        Assert.False(Assert.Single(await analyzer.GetBackupHistoryAsync(ModPath)).CanRestore);
        await Assert.ThrowsAsync<InvalidDataException>(() => analyzer.RestoreBackupAsync(ModPath, remaining.BackupPath));
        Assert.Equal(1, await analyzer.DeleteBackupAsync(ModPath, remaining.BackupPath));
        Assert.Empty(await analyzer.GetBackupsAsync(ModPath));
    }

    [Fact]
    public async Task ReadDetailedAsync_SameVersionDifferentLod_IsAmbiguous()
    {
        WriteGame(GamePath, new Resource(1, UnitType, Unit(lodByte: 1)), new(1, UnitType, Unit(lodByte: 2)));
        var reader = new GameUnitReferenceReader();
        Assert.Equal(GameUnitReferenceStatus.Found, (await reader.ReadAsync(GamePath, [1])).Units[1].Status);
        var detailed = await reader.ReadDetailedAsync(GamePath, [1]);
        Assert.True(detailed.IndexCacheHit);
        Assert.Equal(GameUnitReferenceStatus.Ambiguous, detailed.Units[1].Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task ReadDetailedAsync_TocIncludesTrailingBytes_ReadsReferenceAndReusesDiskIndex(int trailingBytes)
    {
        var unit = Unit();
        var padded = unit.Concat(Enumerable.Repeat((byte)0xa5, trailingBytes)).ToArray();
        WriteGame(GamePath, new Resource(1, UnitType, padded), new(1, UnitType, padded));
        var cache = Path.Combine(_root, "cache");
        var reader = new GameUnitReferenceReader(cache);
        Assert.Equal(GameUnitReferenceStatus.Found, (await reader.ReadAsync(GamePath, [1])).Units[1].Status);

        var report = await new GameUnitReferenceReader(cache).ReadDetailedAsync(GamePath, [1]);

        Assert.True(report.IndexCacheHit);
        Assert.True(report.IndexComplete);
        var reference = report.Units[1];
        Assert.Equal(GameUnitReferenceStatus.Found, reference.Status);
        Assert.Equal(CurrentVersion, reference.Version);
        Assert.Equal(padded, reference.UnitData);
        Assert.Equal(unit.AsSpan(0x78, 16).ToArray(), reference.LodGroupData);
        Assert.Equal(2, reference.Packages.Count);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x5fu)]
    [InlineData(0xfffffff8u)]
    [InlineData(uint.MaxValue)]
    [InlineData(200u)]
    public async Task ReadDetailedAsync_InvalidEnding_RejectsReferenceAndExplainsPreview(uint ending)
    {
        var unit = Unit();
        Put32(unit, 0x60, ending);
        WriteGame(GamePath, new Resource(1, UnitType, unit));
        WritePatch(ModPath, resources: [new(1, UnitType, Unit(1))]);
        var reader = new GameUnitReferenceReader();
        Assert.Equal(GameUnitReferenceStatus.Found, (await reader.ReadAsync(GamePath, [1])).Units[1].Status);

        var reference = (await reader.ReadDetailedAsync(GamePath, [1])).Units[1];

        Assert.Equal(GameUnitReferenceStatus.Error, reference.Status);
        Assert.Null(reference.UnitData);
        Assert.Contains("结束位置越界", reference.ErrorMessage);
        var plan = await new ModCompatibilityAnalyzer(reader).CreateFullRepairPlanAsync(ModPath, GamePath, [], new());
        Assert.Equal(0, plan.ActionCount);
        Assert.Contains(plan.Warnings, warning => warning.Contains("读取失败") && warning.Contains("结束位置越界"));
    }

    [Theory]
    [InlineData(CompatibilityLodStrategy.Automatic, 1u, 9)]
    [InlineData(CompatibilityLodStrategy.UseGameReference, 1u, 9)]
    [InlineData(CompatibilityLodStrategy.PreserveMod, 1u, 3)]
    [InlineData(CompatibilityLodStrategy.Mixed, 1u, 9)]
    [InlineData(CompatibilityLodStrategy.UseGameReference, CurrentVersion, 9)]
    public async Task FullRepair_PaddedGameReference_PreviewsRepairsAndRestores(
        CompatibilityLodStrategy strategy, uint sourceVersion, byte expectedLod)
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(sourceVersion))]);
        var original = File.ReadAllBytes(path);
        var reference = Unit(CurrentVersion, 32, 9).Concat(new byte[8]).ToArray();
        WriteGame(GamePath, new Resource(1, UnitType, reference));
        var analyzer = new ModCompatibilityAnalyzer();
        var options = new CompatibilityRepairOptions { LodStrategy = strategy };

        var plan = await analyzer.CreateFullRepairPlanAsync(ModPath, GamePath, [], options);

        Assert.True(plan.CanRepair, string.Join(";", plan.BlockingReasons.Concat(plan.Warnings)));
        Assert.Empty(plan.Warnings);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(plan.Actions, action => action.Kind == "UnitMigration");
        var result = await analyzer.RepairFullAsync(ModPath, GamePath, [], options);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Changed);
        Assert.Equal(ModCompatibilityStatus.Compatible, (await analyzer.AnalyzeAsync(ModPath, CurrentVersion)).Status);
        var repaired = File.ReadAllBytes(path);
        var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(repaired.AsSpan(104 + 16));
        Assert.Equal(expectedLod, repaired[offset + 0x78]);
        Assert.True((await analyzer.RestoreBackupAsync(ModPath, Assert.Single(result.BackupPaths))).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReferenceIndex_ReusedForDifferentIds_AndInvalidatedByArchiveChange()
    {
        WriteGame(GamePath, Enumerable.Range(1, 80).Select(i => new Resource((ulong)i, UnitType, Unit())).ToArray());
        var reader = new GameUnitReferenceReader();
        var watch = Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(true);
        var first = await reader.ReadAsync(GamePath, [1]);
        var cold = watch.Elapsed;
        var coldBytes = GC.GetTotalAllocatedBytes(true) - before;
        watch.Restart();
        before = GC.GetTotalAllocatedBytes(true);
        var second = await reader.ReadAsync(GamePath, [79, 80]);
        var hot = watch.Elapsed;
        var hotBytes = GC.GetTotalAllocatedBytes(true) - before;
        output.WriteLine($"80 packages: cold={cold.TotalMilliseconds:F3}ms/{coldBytes}B; hot={hot.TotalMilliseconds:F3}ms/{hotBytes}B");
        Assert.False(first.IndexCacheHit);
        Assert.True(second.IndexCacheHit);
        Assert.Equal(2, second.Units.Count);
        // 确认复用的是完整定位索引，不是只缓存上一次所请求 ID 的结果。
        Assert.All(second.Units.Values, unit => Assert.Equal(GameUnitReferenceStatus.Found, unit.Status));
        WriteGame(GamePath, new Resource(1, UnitType, Unit(10800438)));
        var updated = await reader.ReadAsync(GamePath, [1]);
        Assert.False(updated.IndexCacheHit);
        Assert.Equal(10800438u, updated.Units[1].Version);
    }

    [Fact]
    public async Task NoActionPreview_DoesNotCopyOrReadLargeGpuFile()
    {
        var path = WritePatch(ModPath, resources: [new(1, 1, new byte[16], new byte[8])]);
        using (var sparse = new FileStream(path + ".gpu_resources", FileMode.Open, FileAccess.Write)) sparse.SetLength(128L * 1024 * 1024);
        using var gpuLock = new FileStream(path + ".gpu_resources", FileMode.Open, FileAccess.Read, FileShare.None);
        var before = GC.GetTotalAllocatedBytes(true);
        var plan = await new ModCompatibilityAnalyzer().CreateFullRepairPlanAsync(ModPath, null, [], MetadataOnly);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        output.WriteLine($"128MiB locked GPU, no-action preview: allocated={allocated}B");
        Assert.Empty(plan.BlockingReasons);
        Assert.Equal(0, plan.ActionCount);
        Assert.True(allocated < 16L * 1024 * 1024, $"Allocated {allocated} bytes");
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Fact]
    public async Task FullRepair_PreCanceled_CreatesNoStaging()
    {
        WritePatch(ModPath, resources: [new(1, UnitType, Unit())]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModCompatibilityAnalyzer()
            .RepairFullAsync(ModPath, null, [], new(), cancellation.Token));
        Assert.Single(Directory.GetFiles(ModPath));
    }

    [Fact]
    public async Task FullRepair_IdenticalMainDifferentCompanions_RejectsAmbiguousSource()
    {
        var path = WritePatch(ModPath, companions: false, resources: [new(1, 1, [1], [2])]);
        var source1 = Path.Combine(_root, "source1");
        var source2 = Path.Combine(_root, "source2");
        WritePatch(source1, resources: [new(1, 1, [1], [2])]);
        WritePatch(source2, resources: [new(1, 1, [1], [3])]);
        var original = File.ReadAllBytes(path);
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, null, [source1, source2], new() { RepairUnits = false });
        Assert.False(result.Success);
        Assert.Contains("不同伴随内容", result.ErrorMessage);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".gpu_resources"));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Theory]
    [InlineData(CompatibilityLodStrategy.Automatic)]
    [InlineData(CompatibilityLodStrategy.Mixed)]
    public async Task FullRepair_PerUnitLodDecision_DoesNotSpreadCustomMeshToOtherUnits(CompatibilityLodStrategy strategy)
    {
        byte[] MeshUnit(uint version, uint mesh, byte lod)
        {
            var unit = Unit(version, 16, lod);
            Put32(unit, 0x64, 0x88);
            Put32(unit, 0x88, 1);
            Put32(unit, 0x90, mesh);
            return unit;
        }
        WritePatch(ModPath, resources: [new(1, UnitType, MeshUnit(1, 99, 3)), new(2, UnitType, MeshUnit(1, 1, 3))]);
        WriteGame(GamePath, new Resource(1, UnitType, MeshUnit(CurrentVersion, 1, 9)), new(2, UnitType, MeshUnit(CurrentVersion, 1, 9)));
        var options = new CompatibilityRepairOptions { LodStrategy = strategy, PreserveModLodUnitIds = new HashSet<ulong> { 1 } };
        var analyzer = new ModCompatibilityAnalyzer();
        var plan = await analyzer.CreateFullRepairPlanAsync(ModPath, GamePath, [], options);
        Assert.True(plan.CanRepair, string.Join(";", plan.BlockingReasons));
        Assert.Equal(CompatibilityLodStrategy.PreserveMod, plan.Actions.Single(a => a.ResourceId == 1).LodStrategy);
        Assert.Equal(CompatibilityLodStrategy.UseGameReference, plan.Actions.Single(a => a.ResourceId == 2).LodStrategy);
        Assert.True((await analyzer.RepairFullAsync(ModPath, GamePath, [], options)).Success);
    }

    [Fact]
    public async Task FullRepair_UnalignedGameLod_RejectsWithoutWriting()
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(1))]);
        var original = File.ReadAllBytes(path);
        WriteGame(GamePath, new Resource(1, UnitType, Unit(CurrentVersion, 17)));
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, GamePath, [], new() { LodStrategy = CompatibilityLodStrategy.UseGameReference });
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Theory]
    [InlineData(CurrentVersion, true)]
    [InlineData(CurrentVersion + 1, false)]
    public async Task FullRepair_AutomaticCurrentOrUnknownVersion_DoesNotOverwriteAuthorLod(uint version, bool success)
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(version, lodByte: 3))]);
        var original = File.ReadAllBytes(path);
        WriteGame(GamePath, new Resource(1, UnitType, Unit(lodByte: 9)));
        var result = await new ModCompatibilityAnalyzer().RepairFullAsync(ModPath, GamePath, [], new());
        Assert.Equal(success, result.Success);
        Assert.False(result.Changed);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
    }

    [Fact]
    public async Task FullRepair_CanceledAfterMetadataStaging_CleansCurrentMod()
    {
        var path = WritePatch(ModPath, resources: [new(1, UnitType, Unit(1))]);
        var bytes = File.ReadAllBytes(path);
        Put32(bytes, 104 + 76, 7);
        File.WriteAllBytes(path, bytes);
        using var cancellation = new CancellationTokenSource();
        var reader = new Mock<IGameUnitReferenceReader>();
        reader.Setup(r => r.ReadDetailedAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<ulong>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cancellation.Cancel(); return Task.FromCanceled<GameUnitReferenceReport>(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModCompatibilityAnalyzer(reader.Object)
            .RepairFullAsync(ModPath, GamePath, [], new(), cancellation.Token));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(ModPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(ModPath, "*.hd2mimnt-backup"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
