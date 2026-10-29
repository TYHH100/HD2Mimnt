using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.ModelPreview;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ModelPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Preview-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IModRepository> _mods = new();
    private readonly Mock<IProfileRepository> _profiles = new();
    private readonly Mock<IModelPreviewReader> _reader = new();
    private readonly Mod _mod;
    private readonly ModelPreviewService _service;

    public ModelPreviewServiceTests()
    {
        Directory.CreateDirectory(_root);
        _mod = new() { Id = Guid.NewGuid(), Path = _root, Name = "Preview test" };
        _mods.Setup(repo => repo.GetByIdAsync(_mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_mod);
        _reader.Setup(reader => reader.PreviewModelAsync(It.IsAny<DirectoryInfo>(), It.IsAny<IReadOnlyList<FileInfo>>(),
            false, It.IsAny<CancellationToken>())).ReturnsAsync(new ModelPreviewResult());
        _service = new(_mods.Object, _profiles.Object, _reader.Object);
    }

    [Fact]
    public async Task GetSourceAsync_ProfileSelectionRemainsIndependentFromLibraryDefaults()
    {
        _mod.Manifest = new() { ManifestVersion = ManifestVersion.V1,
            Options = [new() { Name = "Body", Include = ["body"] }, new() { Name = "Hat", Include = ["hat"] }] };
        var profile = new Profile { Id = Guid.NewGuid(), Mods = [new() { ModId = _mod.Id,
            OptionsJson = new ProfileModOptionsState { OptionToggles = [true, false] }.ToJson() }] };
        _profiles.Setup(repo => repo.GetByIdAsync(profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        var fromProfile = await _service.GetSourceAsync(_mod.Id, profile.Id);
        var fromLibrary = await _service.GetSourceAsync(_mod.Id);
        Assert.Equal([true, false], fromProfile.Options.OptionToggles);
        Assert.Equal([true, true], fromLibrary.Options.OptionToggles);
        fromProfile.Options.OptionToggles![0] = false;
        Assert.Equal([true, false], ProfileModOptionsState.FromJson(profile.Mods[0].OptionsJson).OptionToggles);
        _profiles.Verify(repo => repo.UpdateAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_UsesDeploymentOrderAndOnlySelectedDirectories()
    {
        _mod.Manifest = new() { ManifestVersion = ManifestVersion.V1, Options = [
            new() { Name = "Body", Include = ["body"] },
            new() { Name = "Variant", SubOptions = [new() { Name = "A", Include = ["a"] }, new() { Name = "B", Include = ["b"] }] },
            new() { Name = "Hidden", Include = ["hidden"] }] };
        foreach (var (folder, number) in new[] { ("body", 2), ("body", 10), ("a", 0), ("b", 0), ("hidden", 0), ("body/nested", 0) })
            CompatibilityFixture.WritePatch(Path.Combine(_root, folder), $"1234567890abcdef.patch_{number}", resources: []);
        IReadOnlyList<FileInfo>? selected = null;
        _reader.Setup(reader => reader.PreviewModelAsync(It.IsAny<DirectoryInfo>(), It.IsAny<IReadOnlyList<FileInfo>>(),
            false, It.IsAny<CancellationToken>())).Callback<DirectoryInfo, IReadOnlyList<FileInfo>, bool, CancellationToken>((_, files, _, _) => selected = files)
            .ReturnsAsync(new ModelPreviewResult());
        var state = new ProfileModOptionsState { OptionToggles = [true, true, false], OptionSelections = [0, 1, 0] };
        await _service.LoadAsync(_mod.Id, state.ToJson());
        Assert.Equal(["body/1234567890abcdef.patch_2", "body/1234567890abcdef.patch_10", "b/1234567890abcdef.patch_0"],
            selected!.Select(file => Path.GetRelativePath(_root, file.FullName).Replace('\\', '/')));
    }

    [Fact]
    public async Task LoadAsync_AllDisabledDoesNotFallBackToWholeMod()
    {
        _mod.Manifest = new() { ManifestVersion = ManifestVersion.V1, Options = [new() { Name = "Body", Include = ["body"] }] };
        CompatibilityFixture.WritePatch(_root, resources: []);
        await _service.LoadAsync(_mod.Id, new ProfileModOptionsState { OptionToggles = [false] }.ToJson());
        _reader.Verify(reader => reader.PreviewModelAsync(It.IsAny<DirectoryInfo>(),
            It.Is<IReadOnlyList<FileInfo>>(files => files.Count == 0), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reader_CancellationPropagatesAndBrokenPatchIsReported()
    {
        var reader = new PatchResourceInspectionService();
        var path = Path.Combine(_root, "1234567890abcdef.patch_0");
        await File.WriteAllBytesAsync(path, [0, 1, 2]);
        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(path)]);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Meshes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.PreviewModelAsync(new DirectoryInfo(_root),
            [new FileInfo(path)], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Reader_EmptyPatchAndMissingCompanionDoNotInventGeometry()
    {
        var reader = new PatchResourceInspectionService();
        var empty = CompatibilityFixture.WritePatch(_root, resources: []);
        Assert.Empty((await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(empty)])).Meshes);
        var missing = CompatibilityFixture.WritePatch(_root, companions: false, resources: [CompatibilityFixture.GpuUnit()]);
        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(missing)]);
        Assert.Empty(result.Meshes);
    }

    [Fact]
    public async Task Reader_LaterPatchOverridesSameUnitAndPreservesOtherUnits()
    {
        var reader = new PatchResourceInspectionService();
        var directory = new DirectoryInfo(_root);
        var earlier = CompatibilityFixture.WritePatch(_root, "1234567890abcdef.patch_10",
            resources: [TriangleUnit(1, 0), TriangleUnit(2, 20)]);
        var later = CompatibilityFixture.WritePatch(_root, "1234567890abcdef.patch_2",
            resources: [TriangleUnit(1, 10)]);

        // 生效顺序来自调用方的补丁链，不是文件名排序；不同坐标可识别实际读取的 GPU 数据。
        var result = await reader.PreviewModelAsync(directory, [new FileInfo(earlier), new FileInfo(later)]);
        Assert.Null(result.Error);
        Assert.Equal(2, result.PatchFileCount);
        Assert.Equal(2, result.Meshes.Count);
        AssertTriangle(Assert.Single(result.Meshes, mesh => mesh.UnitId == 1), 1, Path.GetFileName(later), 10);
        AssertTriangle(Assert.Single(result.Meshes, mesh => mesh.UnitId == 2), 2, Path.GetFileName(earlier), 20);

        var reversed = await reader.PreviewModelAsync(directory, [new FileInfo(later), new FileInfo(earlier)]);
        Assert.Null(reversed.Error);
        Assert.Equal(2, reversed.PatchFileCount);
        Assert.Equal(2, reversed.Meshes.Count);
        AssertTriangle(Assert.Single(reversed.Meshes, mesh => mesh.UnitId == 1), 1, Path.GetFileName(earlier), 0);
        AssertTriangle(Assert.Single(reversed.Meshes, mesh => mesh.UnitId == 2), 2, Path.GetFileName(earlier), 20);
    }

    [Fact]
    public async Task Reader_LaterEmptyUnitDoesNotFallBackToEarlierGeometry()
    {
        var reader = new PatchResourceInspectionService();
        var directory = new DirectoryInfo(_root);
        var earlier = CompatibilityFixture.WritePatch(_root, "1234567890abcdef.patch_0",
            resources: [TriangleUnit(1, 0), TriangleUnit(2, 20)]);
        var later = CompatibilityFixture.WritePatch(_root, "1234567890abcdef.patch_1",
            resources: [new(1, CompatibilityFixture.UnitType, CompatibilityFixture.Unit())]);

        var baseline = await reader.PreviewModelAsync(directory, [new FileInfo(earlier)]);
        Assert.Null(baseline.Error);
        Assert.Equal(2, baseline.Meshes.Count);
        AssertTriangle(Assert.Single(baseline.Meshes, mesh => mesh.UnitId == 1), 1, Path.GetFileName(earlier), 0);

        var result = await reader.PreviewModelAsync(directory, [new FileInfo(earlier), new FileInfo(later)]);
        Assert.Null(result.Error);
        Assert.Equal(2, result.PatchFileCount);
        // 空 Unit 只移除同 ID 的网格，不能回退本体，也不能误删其它 Unit。
        AssertTriangle(Assert.Single(result.Meshes), 2, Path.GetFileName(earlier), 20);
    }

    [Fact]
    public async Task LoadTexturesAsync_LoadsOnlyDistinctRequestedIdsAndContinuesAfterBadTexture()
    {
        var broken = Texture(1);
        var earlier = Texture(2);
        var later = Texture(2, patchOrder: 1);
        var good = Texture(3);
        var unrequested = Texture(4);
        var laterPreview = new TexturePreviewData { Width = 1, Height = 1, BgraPixels = [1, 2, 3, 255], Description = "Later" };
        var goodPreview = new TexturePreviewData { Width = 1, Height = 1, BgraPixels = [4, 5, 6, 255], Description = "Good" };
        var calls = new List<(string Root, TextureInspectionItem Texture, int Pixels, CancellationToken Token)>();
        using var cancellation = new CancellationTokenSource();
        _reader.Setup(reader => reader.PreviewTextureAsync(It.IsAny<DirectoryInfo>(), It.IsAny<TextureInspectionItem>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DirectoryInfo, TextureInspectionItem, int, CancellationToken>((root, texture, pixels, token) =>
                calls.Add((root.FullName, texture, pixels, token)))
            .Returns<DirectoryInfo, TextureInspectionItem, int, CancellationToken>((_, texture, _, _) =>
                ReferenceEquals(texture, broken)
                    ? Task.FromException<TexturePreviewData?>(new InvalidDataException("Synthetic broken texture"))
                    : Task.FromResult<TexturePreviewData?>(ReferenceEquals(texture, later) ? laterPreview : goodPreview));

        // 元数据顺序故意与 PatchOrder 不同；未知 ID、未请求 ID 以及重复请求都不能触发额外解码。
        var result = await _service.LoadTexturesAsync(_mod.Id, [later, broken, good, earlier, unrequested],
            [999, 1, 1, 2, 2, 3], maxDimension: 512, cancellationToken: cancellation.Token);

        Assert.Equal(2, result.Count);
        Assert.Same(laterPreview, result[2]);
        Assert.Same(goodPreview, result[3]);
        Assert.Equal(new[] { broken, later, good }, calls.Select(call => call.Texture));
        Assert.All(calls, call =>
        {
            Assert.Equal(_root, call.Root);
            Assert.Equal(512 * 512, call.Pixels);
            Assert.Equal(cancellation.Token, call.Token);
        });
    }

    [Fact]
    public async Task LoadTexturesAsync_PassesRemaining64MiBBudgetAndStopsWhenFull()
    {
        var textures = Enumerable.Range(1, 6).Select(id => Texture((ulong)id)).ToArray();
        // 仅模拟解码后的尺寸，不分配 64 MiB 像素。无 BGRA 数组时按 Width * Height * 4 计费。
        var full = new TexturePreviewData { Width = 2048, Height = 2048, EncodedImageBytes = [1], Description = "Size-only fixture" };
        var almostFull = new TexturePreviewData { Width = 2048, Height = 2047, EncodedImageBytes = [1], Description = "Size-only fixture" };
        var remainder = new TexturePreviewData { Width = 64, Height = 32, EncodedImageBytes = [1], Description = "Size-only fixture" };
        var calls = new List<(ulong Id, int Pixels)>();
        _reader.Setup(reader => reader.PreviewTextureAsync(It.IsAny<DirectoryInfo>(), It.IsAny<TextureInspectionItem>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DirectoryInfo, TextureInspectionItem, int, CancellationToken>((_, texture, pixels, _) =>
                calls.Add((texture.TextureId, pixels)))
            .Returns<DirectoryInfo, TextureInspectionItem, int, CancellationToken>((_, texture, _, _) =>
                Task.FromResult<TexturePreviewData?>(texture.TextureId switch
                {
                    <= 3 => full,
                    4 => almostFull,
                    _ => remainder
                }));

        var result = await _service.LoadTexturesAsync(_mod.Id, textures,
            textures.Select(texture => texture.TextureId).ToArray(), maxDimension: 2048);

        Assert.Equal(5, result.Count);
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, result.Keys.OrderBy(id => id));
        Assert.Equal(new (ulong Id, int Pixels)[]
        {
            (1, 2048 * 2048), (2, 2048 * 2048), (3, 2048 * 2048), (4, 2048 * 2048), (5, 2048)
        }, calls);
        Assert.Same(almostFull, result[4]);
        Assert.Same(remainder, result[5]);
        Assert.Equal(64L * 1024 * 1024, result.Values.Sum(texture => (long)texture.Width * texture.Height * 4));
    }

    [Fact]
    public async Task LoadTexturesAsync_OriginalResolutionUsesSourcePixelCountAsBudget()
    {
        var texture = Texture(1, width: 640, height: 360);
        var pixels = new List<int>();
        _reader.Setup(reader => reader.PreviewTextureAsync(It.IsAny<DirectoryInfo>(), It.IsAny<TextureInspectionItem>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DirectoryInfo, TextureInspectionItem, int, CancellationToken>((_, _, requested, _) => pixels.Add(requested))
            .ReturnsAsync(new TexturePreviewData { Width = 640, Height = 360, BgraPixels = new byte[640 * 360 * 4], Description = "DDS" });

        var result = await _service.LoadTexturesAsync(_mod.Id, [texture], [1],
            maxDimension: ModelPreviewService.OriginalTextureResolution);

        Assert.Single(result);
        Assert.Equal(640 * 360, Assert.Single(pixels));
    }

    [Fact]
    public async Task LoadTexturesAsync_OriginalResolutionAllowsLargerTotalBudget()
    {
        var textures = Enumerable.Range(1, 5).Select(id => Texture((ulong)id, width: 2048, height: 2048)).ToArray();
        _reader.Setup(reader => reader.PreviewTextureAsync(It.IsAny<DirectoryInfo>(), It.IsAny<TextureInspectionItem>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TexturePreviewData
            {
                Width = 2048, Height = 2048, EncodedImageBytes = [1], Description = "Size-only fixture"
            });

        var ids = textures.Select(texture => texture.TextureId).ToArray();
        var capped = await _service.LoadTexturesAsync(_mod.Id, textures, ids, maxDimension: 2048);
        var original = await _service.LoadTexturesAsync(_mod.Id, textures, ids,
            maxDimension: ModelPreviewService.OriginalTextureResolution);

        // 每张按 2048² × 4 字节计费：固定档 64 MiB 只容得下 4 张，原始档 256 MiB 可全部载入。
        Assert.Equal(4, capped.Count);
        Assert.Equal(5, original.Count);
    }

    [Fact]
    public async Task Reader_RejectsPatchAndTexturePathsOutsideModRoot()
    {
        var reader = new PatchResourceInspectionService();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "selected"));
        // 共享字符串前缀的兄弟目录仍在根外；两者都在本测试的临时目录中，Dispose 统一清理。
        var outsideDirectory = Path.Combine(_root, "selected-other");
        var outside = CompatibilityFixture.WritePatch(outsideDirectory, resources: [TriangleUnit(1, 10)]);
        var baseline = await reader.PreviewModelAsync(new DirectoryInfo(outsideDirectory), [new FileInfo(outside)]);
        Assert.Null(baseline.Error);
        AssertTriangle(Assert.Single(baseline.Meshes), 1, Path.GetFileName(outside), 10);

        var result = await reader.PreviewModelAsync(directory, [new FileInfo(outside)]);
        Assert.Equal("路径越界。", result.Error);
        Assert.Empty(result.Meshes);
        Assert.Empty(result.Textures);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.PreviewTextureAsync(directory, Texture(1, patchPath: outside)));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.PreviewTextureAsync(directory,
            Texture(1, patchPath: Path.GetRelativePath(directory.FullName, outside))));
    }

    private static CompatibilityFixture.Resource TriangleUnit(ulong id, float offsetX)
    {
        var resource = CompatibilityFixture.GpuUnit();
        const int streamInfo = 0x98;
        const int vertexBytes = 3 * 3 * sizeof(float);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x160, 3); // 顶点数
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a4, vertexBytes);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a8, vertexBytes); // 索引紧跟顶点
        var gpu = new byte[vertexBytes + 6];
        float[] positions = [offsetX, 0, 0, offsetX + 1, 0, 0, offsetX, 1, 0];
        for (var index = 0; index < positions.Length; index++)
            CompatibilityFixture.Put32(gpu, index * sizeof(float), BitConverter.SingleToUInt32Bits(positions[index]));
        new byte[] { 0, 0, 1, 0, 2, 0 }.CopyTo(gpu, vertexBytes);
        return resource with { Id = id, Gpu = gpu };
    }

    private static void AssertTriangle(ModelPreviewMesh mesh, ulong unitId, string patchFile, float offsetX)
    {
        Assert.Equal(unitId, mesh.UnitId);
        Assert.Equal(patchFile, mesh.PatchFile);
        Assert.Equal(0, mesh.StreamIndex);
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(new float[] { offsetX, 0, 0, offsetX + 1, 0, 0, offsetX, 1, 0 }, mesh.Positions);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.TriangleIndices);
    }

    private TextureInspectionItem Texture(ulong id, int patchOrder = 0, string? patchPath = null,
        int width = 1, int height = 1)
    {
        var path = patchPath ?? Path.Combine(_root, $"1234567890abcdef.patch_{patchOrder}");
        return new TextureInspectionItem
        {
            PatchFile = Path.GetFileName(path), PatchPath = path, PatchOrder = patchOrder, TocEntryIndex = 1,
            TextureId = id, MainOffset = 0, MainSize = 0, GpuOffset = 0, GpuSize = 4,
            StreamOffset = 0, StreamSize = 0, Width = width, Height = height, MipCount = 1,
            DxgiFormat = 28, PayloadKind = "DDS", PayloadSource = "gpu_resources"
        };
    }

    public void Dispose() => Directory.Delete(_root, true);
}
