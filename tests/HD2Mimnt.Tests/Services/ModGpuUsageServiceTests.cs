using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using Moq;
using static HD2Mimnt.Tests.Services.GpuUsageFixture;

namespace HD2Mimnt.Tests.Services;

public sealed class ModGpuUsageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-GpuUsage-{Guid.NewGuid():N}");
    private readonly Profile _profile = new() { Id = Guid.NewGuid() };
    private readonly List<Mod> _mods = [];
    private readonly ModGpuUsageService _service;
    private readonly SettingsService _settings;

    public ModGpuUsageServiceTests()
    {
        Directory.CreateDirectory(_root);
        var profiles = new Mock<IProfileRepository>();
        profiles.Setup(repo => repo.GetByIdAsync(_profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_profile);
        var mods = new Mock<IModRepository>();
        mods.Setup(repo => repo.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _mods);
        _settings = new(_root);
        _service = new(profiles.Object, mods.Object, _settings);
    }

    private Mod AddMod(bool enabled = true)
    {
        var mod = new Mod { Id = Guid.NewGuid(), Path = Path.Combine(_root, _mods.Count.ToString()),
            BslRole = BslModRole.None, BslDetectionVersion = BslModDetector.DetectionVersion };
        Directory.CreateDirectory(mod.Path);
        _mods.Add(mod);
        _profile.Mods.Add(new() { ModId = mod.Id, Enabled = enabled, Order = _mods.Count - 1 });
        return mod;
    }

    [Theory]
    [InlineData(71, 5, 3, 3, 32)] // 每个末端 mip 仍占一个 BC 块。
    [InlineData(80, 4, 4, 3, 24)]
    [InlineData(83, 4, 4, 3, 48)]
    [InlineData(95, 4, 4, 3, 48)]
    [InlineData(98, 4, 4, 3, 48)]
    [InlineData(28, 5, 3, 3, 72)]
    public void Dds_ShouldCalculateFullMipPayload(int format, int width, int height, int mips, long expected)
    {
        Assert.True(DdsTextureMemoryReader.TryCalculate(Dds((uint)format, (uint)width, (uint)height, (uint)mips), out var result));
        Assert.Equal(expected, result.Bytes);
    }

    [Fact]
    public void Dds_ShouldHandleArraysCubesVolumesAndRejectUnknownOrImpossibleHeaders()
    {
        Assert.True(DdsTextureMemoryReader.TryCalculate(Dds(layers: 2, cube: true), out var cube));
        Assert.Equal(576, cube.Bytes);
        Assert.True(DdsTextureMemoryReader.TryCalculate(Dds(depth: 4), out var volume));
        Assert.Equal(112, volume.Bytes);
        Assert.False(DdsTextureMemoryReader.TryCalculate(Dds(format: 999), out _));
        Assert.False(DdsTextureMemoryReader.TryCalculate(Dds(mips: 40), out _));
        Assert.False(DdsTextureMemoryReader.TryCalculate(Dds().AsSpan(0, 140), out _));
    }

    [Fact]
    public async Task Scan_ShouldCountTextureGpuStreamAndInlineButOnlyModelGpuSegments()
    {
        var mod = AddMod();
        WritePatch(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0"),
            new(1, Texture, 16, 16, TextureMain(inlineBytes: 16)), new(2, Unit, 120, 80), new(3, 123, 12, 99));
        var usage = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.True(usage.IsComplete, string.Join("\n", usage.Warnings));
        Assert.Equal(new GpuResourceBytes(48, 120, 12), usage.Bytes);
        Assert.Equal(3, usage.ResourceCount);
    }

    [Fact]
    public async Task Scan_ShouldUseSelectedNestedOptionAndKeepLibraryDefaultIndependent()
    {
        var mod = AddMod();
        mod.Manifest = new() { ManifestVersion = ManifestVersion.V1, Options = [new()
        {
            Name = "Armor", SubOptions = [new() { Name = "Colors", SubOptions =
                [new() { Name = "A", Include = ["a"] }, new() { Name = "B", Include = ["b"] }] }]
        }] };
        WritePatch(Path.Combine(mod.Path, "a", "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Unit, 10));
        WritePatch(Path.Combine(mod.Path, "b", "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Unit, 20));
        WritePatch(Path.Combine(mod.Path, "unselected", "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Unit, 999));
        _profile.Mods[0].OptionsJson = new ProfileModOptionsState
            { OptionToggles = [true], OptionSelections = [0], SubOptionSelections = [[1]] }.ToJson();
        Assert.Equal(10, (await _service.ScanModsAsync([mod.Id]))[mod.Id].Bytes.TotalBytes);
        var profile = await _service.ScanProfileAsync(_profile.Id);
        Assert.True(profile.Total.IsComplete);
        Assert.Equal(20, profile.Total.Bytes.TotalBytes);
        Assert.Equal(20, profile.Mods[mod.Id].Bytes.TotalBytes);
    }

    [Fact]
    public async Task Profile_ShouldApplyNumericPatchOrderResourceKeysAndDeploymentDirection()
    {
        var first = AddMod();
        var second = AddMod();
        var disabled = AddMod(false);
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_2"), new Resource(1, Unit, 200));
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_10"), new(1, Unit, 100), new(1, 123, 7));
        WritePatch(Path.Combine(second.Path, "bbbbbbbbbbbbbbbb.patch_0"), new Resource(1, Unit, 300));
        File.WriteAllText(Path.Combine(disabled.Path, "aaaaaaaaaaaaaaaa.patch_0"), "broken");
        var profile = await _service.ScanProfileAsync(_profile.Id);
        Assert.True(profile.Total.IsComplete);
        Assert.False(profile.Mods[disabled.Id].IsComplete);
        Assert.Equal(307, profile.Total.Bytes.TotalBytes);
        Assert.Equal(7, profile.EffectiveBytes[first.Id].TotalBytes);
        Assert.Equal(300, profile.EffectiveBytes[second.Id].TotalBytes);
        await _settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.BottomToTop.ToString());
        Assert.Equal(107, (await _service.ScanProfileAsync(_profile.Id)).Total.Bytes.TotalBytes);
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_11"), new Resource(1, Unit, 0));
        // 新增补丁由管理器刷新流程作废缓存后才参与统计。
        _service.InvalidateScans();
        Assert.Equal(7, (await _service.ScanProfileAsync(_profile.Id)).Total.Bytes.TotalBytes);
    }

    [Fact]
    public async Task Scan_ShouldReuseModResultsUntilInvalidated()
    {
        var mod = AddMod();
        var path = Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0");
        WritePatch(path, new Resource(1, Unit, 100));
        var first = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.Equal(100, first.Bytes.TotalBytes);
        // 同一模组与选项在模组库页、配置页和重复调用之间只计算一次。
        Assert.Same(first, (await _service.ScanModsAsync([mod.Id]))[mod.Id]);
        Assert.Same(first, (await _service.ScanProfileAsync(_profile.Id)).Mods[mod.Id]);
        // 文件被改写后，管理器刷新（InvalidateScans）才重新读取。
        using (var gpu = File.OpenWrite(path + ".gpu_resources")) gpu.SetLength(1);
        Assert.Same(first, (await _service.ScanModsAsync([mod.Id]))[mod.Id]);
        _service.InvalidateScans();
        Assert.False((await _service.ScanModsAsync([mod.Id]))[mod.Id].IsComplete);
        var stamp = File.GetLastWriteTimeUtc(path);
        WritePatch(path, new Resource(1, Unit, 200));
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(2));
        var refreshed = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.True(refreshed.IsComplete);
        Assert.Equal(200, refreshed.Bytes.TotalBytes);
    }

    [Fact]
    public async Task Scan_ShouldDistinguishExplicitlyEmptySelectionFromUnmappedSubdirectories()
    {
        var mod = AddMod();
        WritePatch(Path.Combine(mod.Path, "nested", "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Unit, 100));
        Assert.False((await _service.ScanModsAsync([mod.Id]))[mod.Id].IsComplete);
        mod.Manifest = new() { ManifestVersion = ManifestVersion.V1,
            Options = [new() { Name = "Optional", Include = ["nested"], DefaultEnabled = false }] };
        var empty = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.True(empty.IsComplete);
        Assert.Equal(0, empty.Bytes.TotalBytes);
    }

    [Fact]
    public async Task Scan_ShouldKeepBrokenSelectedDirectoriesAndUnknownTexturesUnconfirmed()
    {
        var mod = AddMod();
        mod.Manifest = new() { ManifestVersion = ManifestVersion.V1, Options = [new() { Name = "Missing", Include = ["missing"] }] };
        var missing = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.False(missing.IsComplete);
        Assert.Equal(0, missing.ResourceCount);
        mod.Manifest = null;
        WritePatch(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Texture, 100));
        var unknown = (await _service.ScanModsAsync([mod.Id]))[mod.Id];
        Assert.False(unknown.IsComplete);
        File.WriteAllText(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_1"), "broken");
        Assert.False((await _service.ScanProfileAsync(_profile.Id)).Total.IsComplete);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ScanProfileAsync(_profile.Id, cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
