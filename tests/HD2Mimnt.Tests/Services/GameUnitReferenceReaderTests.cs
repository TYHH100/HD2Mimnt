using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using K4os.Compression.LZ4;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class GameUnitReferenceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntGameReferenceTests", Guid.NewGuid().ToString("N"));
    private string GamePath => Path.Combine(_root, "game");
    private string BundlePath => Path.Combine(GamePath, "data", "bundles.00.nxa");

    [Theory]
    [InlineData(96 * 1024 * 1024)]
    [InlineData(898521500)] // 本地真实 bundles.08.nxa 中出现的未压缩块尺寸。
    public async Task Cache_LargeUncompressedBlock_ReadsOnlyRequestedRangesAndReusesDiskIndex(int size)
    {
        WriteGame(false, (1, 123u));
        // 包内 TOC 和 Unit 落在同一个大块的不同位置，保持现有包映射不变。
        using (var file = new FileStream(BundlePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.SetLength(64L + size);
            var header = new byte[64];
            Put32(header, 0, 0x52415344); Put32(header, 8, 1);
            Put64(header, 32, 0x100); Put64(header, 40, 64);
            Put32(header, 48, (uint)size); Put32(header, 52, (uint)size); header[57] = 3;
            file.Write(header); file.Write(Toc(1, 0x5007));
            file.Position = 64 + 0x1007 - 0x100 + 0x2c;
            var version = new byte[4]; Put32(version, 0, 123); file.Write(version);
        }
        var root = Path.Combine(_root, "cache");
        var reader = new GameUnitReferenceReader(root);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var built = await reader.BuildCacheAsync(GamePath);
        var read = await reader.ReadAsync(GamePath, [1]);
        var reused = await new GameUnitReferenceReader(root).ReadAsync(GamePath, [1]);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.Equal(GameReferenceCacheState.Ready, built.State);
        Assert.True(read.IndexComplete); Assert.Empty(read.Issues); Assert.Equal(123u, read.Units[1].Version);
        Assert.True(reused.IndexComplete); Assert.True(reused.IndexCacheHit); Assert.Equal(123u, reused.Units[1].Version);
        Assert.True(bytes < 16 * 1024 * 1024, $"读取小片段不应分配整块内存：实际 {bytes:N0} B，块 {size:N0} B。");
        using var exclusive = new FileStream(BundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Cache_BuildPersistsAcrossReadersAndAutomaticallyRebuildsChangedArchives()
    {
        WriteGame(true, (0xf123456789abcdef, 123u), (2, 456u));
        var cacheRoot = Path.Combine(_root, "cache");
        var reader = new GameUnitReferenceReader(cacheRoot);
        Assert.Equal(GameReferenceCacheState.Missing, (await reader.GetCacheStatusAsync(GamePath)).State);
        var built = await reader.BuildCacheAsync(GamePath);
        Assert.Equal(GameReferenceCacheState.Ready, built.State);
        Assert.Equal(2, built.UnitCount); Assert.Equal(2, built.PackageCount); Assert.True(File.Exists(built.CachePath));
        var reused = await new GameUnitReferenceReader(cacheRoot).ReadAsync(GamePath, [0xf123456789abcdef]);
        Assert.True(reused.IndexCacheHit); Assert.True(reused.IndexComplete);
        Assert.Equal(123u, reused.Units[0xf123456789abcdef].Version);
        WriteGame(true, (0xf123456789abcdef, 789u));
        Assert.Equal(GameReferenceCacheState.Stale, (await reader.GetCacheStatusAsync(GamePath)).State);
        var updated = await new GameUnitReferenceReader(cacheRoot).ReadAsync(GamePath, [0xf123456789abcdef]);
        Assert.True(updated.IndexComplete); Assert.False(updated.IndexCacheHit);
        Assert.Equal(789u, updated.Units[0xf123456789abcdef].Version);
        Assert.Equal(GameReferenceCacheState.Ready, (await reader.GetCacheStatusAsync(GamePath)).State);
    }

    [Fact]
    public async Task Cache_CorruptionRebuildsButFailedOrCanceledBuildPreservesSavedData()
    {
        WriteGame(false, (1, 123u));
        var root = Path.Combine(_root, "cache");
        var reader = new GameUnitReferenceReader(root);
        var built = await reader.BuildCacheAsync(GamePath);
        var saved = File.ReadAllBytes(built.CachePath);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.BuildCacheAsync(GamePath, true, cancellationToken: canceled.Token));
        }
        Assert.Equal(saved, File.ReadAllBytes(built.CachePath));
        var bundle = File.ReadAllBytes(BundlePath); bundle[0] = 0; File.WriteAllBytes(BundlePath, bundle);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.BuildCacheAsync(GamePath, true));
        Assert.Equal(saved, File.ReadAllBytes(built.CachePath));
        WriteGame(false, (1, 456u));
        saved[^1] ^= 0xff; File.WriteAllBytes(built.CachePath, saved);
        var rebuilt = await new GameUnitReferenceReader(root).ReadAsync(GamePath, [1]);
        Assert.True(rebuilt.IndexComplete); Assert.False(rebuilt.IndexCacheHit); Assert.Equal(456u, rebuilt.Units[1].Version);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(built.CachePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_RealDsarBlocks_ReadsCompressedAndCrossBlockVersion(bool compressed)
    {
        WriteGame(compressed, (0xf123456789abcdef, 123u), (2, 456u));
        var reader = new GameUnitReferenceReader();
        var result = await reader.ReadAsync(GamePath, [0xf123456789abcdef, 2, 99]);
        Assert.True(result.IndexComplete);
        Assert.Empty(result.Issues);
        Assert.Equal(123u, result.Units[0xf123456789abcdef].Version);
        Assert.Equal(456u, result.Units[2].Version);
        Assert.Equal(GameUnitReferenceStatus.Missing, result.Units[99].Status);
        Assert.Equal(["base_000"], result.Units[0xf123456789abcdef].Packages);
        // 所有句柄均应在调用完成后释放。
        using var exclusive = new FileStream(BundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(123u, GameUnitReferenceStatus.Found)]
    [InlineData(456u, GameUnitReferenceStatus.Ambiguous)]
    public async Task ReadAsync_DuplicateResource_UsesVersionAgreement(uint secondVersion, GameUnitReferenceStatus expected)
    {
        WriteGame(true, (1, 123u), (1, secondVersion));
        var result = await new GameUnitReferenceReader().ReadAsync(GamePath, [1]);
        Assert.True(result.IndexComplete);
        Assert.Equal(expected, result.Units[1].Status);
        Assert.Equal(2, result.Units[1].Packages.Count);
        Assert.Equal(expected == GameUnitReferenceStatus.Found ? 123u : (uint?)null, result.Units[1].Version);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("magic")]
    [InlineData("range")]
    [InlineData("overlap")]
    [InlineData("raw-size")]
    [InlineData("compression")]
    [InlineData("package-table")]
    public async Task ReadAsync_InvalidArchive_ReturnsIncompleteReference(string fault)
    {
        WriteGame(false, (1, 123u));
        if (fault == "missing") File.Delete(BundlePath);
        else
        {
            var path = fault == "package-table" ? Path.Combine(GamePath, "data", "bundles.nxa") : BundlePath;
            var bytes = File.ReadAllBytes(path);
            if (fault == "magic") bytes[0] = 0;
            if (fault == "range") Put64(bytes, 32 + 8, ulong.MaxValue);
            if (fault == "overlap") Put64(bytes, 32 + 32, 0x100);
            if (fault == "raw-size") Put32(bytes, 32 + 20, 183);
            if (fault == "compression") bytes[32 + 24] = 7;
            // 第一个索引块保存 31 字节；第二个块内的第一个包记录 itemsOffset 位于逻辑偏移 40。
            if (fault == "package-table") Put32(bytes, 96 + 31 + (40 - 31), uint.MaxValue);
            File.WriteAllBytes(path, bytes);
        }
        var result = await new GameUnitReferenceReader().ReadAsync(GamePath, [1]);
        Assert.False(result.IndexComplete);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(GameUnitReferenceStatus.Error, result.Units[1].Status);
        Assert.Null(result.Units[1].Version);
    }

    [Fact]
    public async Task ReadAsync_BrokenUnit_PreservesOtherReadableReferences()
    {
        WriteGame(false, (1, 123u), (2, 456u));
        var bytes = File.ReadAllBytes(BundlePath);
        // 每包三个块：TOC、Unit 前段、Unit 后段。只损坏第一个 Unit 的压缩标记。
        bytes[32 + 32 + 24] = 7;
        File.WriteAllBytes(BundlePath, bytes);
        var result = await new GameUnitReferenceReader().ReadAsync(GamePath, [1, 2]);
        Assert.True(result.IndexComplete);
        Assert.Equal(GameUnitReferenceStatus.Error, result.Units[1].Status);
        Assert.Equal(GameUnitReferenceStatus.Found, result.Units[2].Status);
        Assert.Equal(456u, result.Units[2].Version);
    }

    [Fact]
    public async Task ReadAsync_ArchiveReplaced_ReadsNewVersion()
    {
        var reader = new GameUnitReferenceReader();
        WriteGame(true, (1, 123u));
        Assert.Equal(123u, (await reader.ReadAsync(GamePath, [1])).Units[1].Version);
        WriteGame(true, (1, 456u));
        Assert.Equal(456u, (await reader.ReadAsync(GamePath, [1])).Units[1].Version);
    }

    [Fact]
    public async Task ReadAsync_PreCanceled_DoesNotReadArchives()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GameUnitReferenceReader()
            .ReadAsync(GamePath, [1], cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CheckModsAsync_MultipleGameVersions_ComparesEachIdAndLoadsReferencesOnce()
    {
        WriteGame(true, (1, 123u), (2, 456u));
        var mods = new[] { CreateMod(1, 123), CreateMod(2, 456), CreateMod(1, 999), CreateMod(99, 123) };
        var reader = new Mock<IGameUnitReferenceReader>();
        reader.Setup(r => r.ReadAsync(GamePath, It.IsAny<IReadOnlyCollection<ulong>>(), It.IsAny<CancellationToken>()))
            .Returns((string path, IReadOnlyCollection<ulong> ids, CancellationToken token) =>
                new GameUnitReferenceReader().ReadAsync(path, ids, token));
        var service = CreateService(mods, reader.Object);
        var reports = await service.CheckModsAsync(mods.Select(mod => mod.Id));
        Assert.Equal(new[] { ModCompatibilityStatus.Compatible, ModCompatibilityStatus.Compatible,
            ModCompatibilityStatus.Incompatible, ModCompatibilityStatus.Unknown }, reports.Select(r => r.Status));
        Assert.All(reports, report => Assert.Null(report.ReferenceVersion));
        Assert.Equal(456u, reports[1].Units[0].ReferenceVersion);
        reader.Verify(r => r.ReadAsync(GamePath,
            It.Is<IReadOnlyCollection<ulong>>(ids => ids.Count == 3 && ids.Contains(99UL)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckModAsync_AmbiguousReference_KeepsUnknownAndExplicitOverrideStillWorks()
    {
        WriteGame(false, (1, 123u), (1, 456u));
        var mod = CreateMod(1, 123);
        var service = CreateService([mod], new GameUnitReferenceReader());
        var report = await service.CheckModAsync(mod.Id);
        Assert.Equal(ModCompatibilityStatus.Unknown, report.Status);
        Assert.Equal(ModCompatibilityReason.NoComparableUnits, report.Reason);
        Assert.Equal(GameUnitReferenceStatus.Ambiguous, report.Units[0].ReferenceStatus);
        Assert.Equal(ModCompatibilityStatus.Compatible, (await service.CheckModAsync(mod.Id, 123)).Status);
    }

    [Theory]
    [InlineData(GameUnitReferenceStatus.Missing, 123u, ModCompatibilityStatus.Compatible)]
    [InlineData(GameUnitReferenceStatus.Ambiguous, 123u, ModCompatibilityStatus.Compatible)]
    [InlineData(GameUnitReferenceStatus.Error, 123u, ModCompatibilityStatus.Compatible)]
    [InlineData(GameUnitReferenceStatus.Missing, 456u, ModCompatibilityStatus.Incompatible)]
    public async Task CheckModAsync_PartialReferences_UsesComparableModelsAndRetainsMissingDetails(
        GameUnitReferenceStatus missingStatus, uint version, ModCompatibilityStatus expected)
    {
        var mod = CreateMod(1, version);
        CompatibilityFixture.WritePatch(mod.Path, resources:
            [new(1, CompatibilityFixture.UnitType, CompatibilityFixture.Unit(version)),
             new(99, CompatibilityFixture.UnitType, CompatibilityFixture.Unit(123))]);
        var reader = new Mock<IGameUnitReferenceReader>();
        reader.Setup(r => r.ReadAsync(GamePath, It.IsAny<IReadOnlyCollection<ulong>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameUnitReferenceReport
            {
                IndexComplete = true,
                Units = new()
                {
                    [1] = new() { ResourceId = 1, Status = GameUnitReferenceStatus.Found, Version = 123 },
                    [99] = new() { ResourceId = 99, Status = missingStatus }
                }
            });
        var report = await CreateService([mod], reader.Object).CheckModAsync(mod.Id);
        Assert.Equal(expected, report.Status); Assert.Equal(1, report.ComparedUnitCount); Assert.Equal(2, report.UnitCount);
        Assert.Equal(missingStatus, report.Units.Single(unit => unit.ResourceId == 99).ReferenceStatus);
        Assert.Contains(report.Issues, issue => issue.Contains("0000000000000063"));
    }

    [Fact]
    public async Task CheckModAsync_MatchedModelWithUnknownGpuFormat_PreservesVersionResultAndWarning()
    {
        var mod = CreateMod(1, CompatibilityFixture.CurrentVersion);
        CompatibilityFixture.WritePatch(mod.Path, resources: [CompatibilityFixture.GpuUnit(unknownFormat: true)]);
        var reader = new Mock<IGameUnitReferenceReader>();
        reader.Setup(r => r.ReadAsync(GamePath, It.IsAny<IReadOnlyCollection<ulong>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameUnitReferenceReport
            {
                IndexComplete = true,
                Units = new() { [1] = new() { ResourceId = 1, Status = GameUnitReferenceStatus.Found, Version = CompatibilityFixture.CurrentVersion } }
            });
        var report = await CreateService([mod], reader.Object).CheckModAsync(mod.Id);
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.Equal(1, report.ComparedUnitCount); Assert.NotEmpty(report.Units[0].Analysis!.Warnings);
    }

    [Fact]
    public async Task CheckModAsync_MissingGameArchive_DoesNotTurnStructuralDiagnosticsIntoVersionMismatch()
    {
        var valid = CreateMod(1, 123);
        var broken = CreateMod(2, 123);
        var path = Path.Combine(broken.Path, "abcdeffedcba9876.patch_0");
        var bytes = File.ReadAllBytes(path);
        Put32(bytes, 104 + 76, 9);
        File.WriteAllBytes(path, bytes);
        var service = CreateService([valid, broken], new GameUnitReferenceReader());
        Assert.Equal(ModCompatibilityStatus.Unknown, (await service.CheckModAsync(valid.Id)).Status);
        var report = await service.CheckModAsync(broken.Id);
        Assert.Equal(ModCompatibilityStatus.Unknown, report.Status);
        Assert.Equal(ModCompatibilityReason.ReferenceUnavailable, report.Reason);
        Assert.True(report.HasBlockingIssues);
    }

    [Theory]
    [InlineData("entry_index", CompatibilityFixture.CurrentVersion, ModCompatibilityStatus.Compatible)]
    [InlineData("weights", CompatibilityFixture.CurrentVersion, ModCompatibilityStatus.Compatible)]
    [InlineData("mixed_audio", CompatibilityFixture.CurrentVersion, ModCompatibilityStatus.Compatible)]
    [InlineData("entry_index", 1u, ModCompatibilityStatus.Incompatible)]
    public async Task CheckModAsync_DeepDiagnostics_DoNotOverrideModelVersionComparison(
        string diagnostic, uint referenceVersion, ModCompatibilityStatus expected)
    {
        var mod = CreateMod(1, CompatibilityFixture.CurrentVersion);
        var resources = new List<CompatibilityFixture.Resource>
        {
            diagnostic == "weights" ? CompatibilityFixture.GpuUnit(unnormalizedWeights: true)
                : new(1, CompatibilityFixture.UnitType, CompatibilityFixture.Unit())
        };
        if (diagnostic == "mixed_audio") resources.Add(new(99, 0x535a7bd3e650d799UL, new byte[16], Stream: new byte[8]));
        var path = CompatibilityFixture.WritePatch(mod.Path, companions: diagnostic != "mixed_audio", resources: resources.ToArray());
        if (diagnostic == "entry_index")
        {
            var bytes = File.ReadAllBytes(path);
            Put32(bytes, 180, 0);
            File.WriteAllBytes(path, bytes);
        }
        var before = File.ReadAllBytes(path);
        var reader = new Mock<IGameUnitReferenceReader>();
        reader.Setup(r => r.ReadAsync(GamePath, It.IsAny<IReadOnlyCollection<ulong>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameUnitReferenceReport
            {
                IndexComplete = true,
                Units = new() { [1] = new() { ResourceId = 1, Status = GameUnitReferenceStatus.Found, Version = referenceVersion } }
            });
        var report = await CreateService([mod], reader.Object).CheckModAsync(mod.Id);
        Assert.Equal(expected, report.Status);
        Assert.True(report.HasBlockingIssues);
        Assert.Equal(1, report.ComparedUnitCount);
        Assert.NotEmpty(report.Issues);
        if (diagnostic == "weights") Assert.Contains(report.Units[0].Analysis!.Errors, issue => issue.Contains("权重总和"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private ModCompatibilityService CreateService(Mod[] mods, IGameUnitReferenceReader reader)
    {
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        foreach (var mod in mods)
            repository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        var settingsRepository = new Mock<ISettingsRepository>();
        settingsRepository.Setup(r => r.IsLegacyMigrationComplete()).Returns(true);
        settingsRepository.Setup(r => r.ReadAll()).Returns(new Dictionary<string, string> { ["Game.Path"] = JsonSerializer.Serialize(GamePath) });
        var settings = new SettingsService(settingsRepository.Object, _root);
        return new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(),
            new ModService(repository.Object, Path.Combine(_root, "storage")), reader, settings);
    }

    private Mod CreateMod(ulong id, uint version)
    {
        var mod = new Mod { Id = Guid.NewGuid(), Path = Path.Combine(_root, Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(mod.Path);
        var bytes = Toc(id, 184);
        Array.Resize(ref bytes, 184 + 0x80);
        Put32(bytes, 160, 0x80);
        Put32(bytes, 184 + 0x2c, version);
        Put32(bytes, 184 + 0x60, 0x78);
        File.WriteAllBytes(Path.Combine(mod.Path, "abcdeffedcba9876.patch_0"), bytes);
        return mod;
    }

    private void WriteGame(bool compressed, params (ulong Id, uint Version)[] units)
    {
        Directory.CreateDirectory(Path.Combine(GamePath, "data"));
        var namesStart = 24 + units.Length * 24;
        var itemsStart = namesStart + units.Length * 9;
        var index = new byte[itemsStart + units.Length * 32];
        Put32(index, 12, 1);
        Put32(index, 16, (uint)units.Length);
        var chunks = new List<(ulong Offset, byte[] Data, byte Flags)>();
        for (var i = 0; i < units.Length; i++)
        {
            var tocOffset = (uint)(0x100 + i * 0x10000);
            var unitOffset = (uint)(0x1000 + i * 0x10000);
            var record = 24 + i * 24;
            Put32(index, record + 8, (uint)(namesStart + i * 9));
            Put32(index, record + 12, 2);
            Put32(index, record + 16, (uint)(itemsStart + i * 32));
            Encoding.UTF8.GetBytes($"base_{i:D3}\0").CopyTo(index, namesStart + i * 9);
            var item = itemsStart + i * 32;
            Put64(index, item, 0);
            Put32(index, item + 8, tocOffset);
            Put64(index, item + 16, 0x5000);
            Put32(index, item + 24, unitOffset);
            chunks.Add((tocOffset, Toc(units[i].Id, 0x5007), 2));
            var unit = new byte[7 + 0x30];
            Put32(unit, 7 + 0x2c, units[i].Version);
            chunks.Add((unitOffset, unit[..39], 2));
            chunks.Add((unitOffset + 39UL, unit[39..], 0));
        }
        WriteDsar(BundlePath, chunks, compressed);
        WriteDsar(Path.Combine(GamePath, "data", "bundles.nxa"),
            [(0, index[..31], 0), (31, index[31..], 0)], compressed);
    }

    private static byte[] Toc(ulong id, ulong resourceOffset)
    {
        var bytes = new byte[184];
        Put32(bytes, 0, 0xf0000011);
        Put32(bytes, 4, 1);
        Put32(bytes, 8, 1);
        Put64(bytes, 80, 0xe0a48d0be9a7453f);
        Put64(bytes, 88, 1);
        Put64(bytes, 104, id);
        Put64(bytes, 112, 0xe0a48d0be9a7453f);
        Put64(bytes, 120, resourceOffset);
        Put32(bytes, 160, 0x30);
        Put32(bytes, 180, 1);
        return bytes;
    }

    private static void WriteDsar(string path, IReadOnlyList<(ulong Offset, byte[] Data, byte Flags)> chunks, bool compressed)
    {
        var encoded = chunks.Select(chunk =>
        {
            if (!compressed) return chunk.Data;
            var buffer = new byte[LZ4Codec.MaximumOutputSize(chunk.Data.Length)];
            var length = LZ4Codec.Encode(chunk.Data, buffer);
            Assert.True(length > 0);
            return buffer[..length];
        }).ToArray();
        var dataOffset = 32 + chunks.Count * 32;
        var output = new byte[dataOffset + encoded.Sum(chunk => chunk.Length)];
        Put32(output, 0, 0x52415344);
        Put32(output, 8, (uint)chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var entry = 32 + i * 32;
            Put64(output, entry, chunks[i].Offset);
            Put64(output, entry + 8, (ulong)dataOffset);
            Put32(output, entry + 16, (uint)chunks[i].Data.Length);
            Put32(output, entry + 20, (uint)encoded[i].Length);
            output[entry + 24] = compressed ? (byte)3 : (byte)0;
            output[entry + 25] = chunks[i].Flags;
            encoded[i].CopyTo(output, dataOffset);
            dataOffset += encoded[i].Length;
        }
        File.WriteAllBytes(path, output);
    }

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
