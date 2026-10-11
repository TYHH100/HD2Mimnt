using System.Buffers.Binary;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ConflictDetectionServiceTests : IDisposable
{
    private const ulong Texture = 0xcd4238c6a0c69e32;
    private const ulong Unit = 0xe0a48d0be9a7453f;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Conflicts-{Guid.NewGuid():N}");
    private readonly Profile _profile = new() { Id = Guid.NewGuid(), Name = "测试配置" };
    private readonly List<Mod> _mods = new();
    private readonly ConflictDetectionService _scanner;
    private readonly SettingsService _settings;

    public ConflictDetectionServiceTests()
    {
        Directory.CreateDirectory(_root);
        var profiles = new Mock<IProfileRepository>();
        var mods = new Mock<IModRepository>();
        profiles.Setup(repo => repo.GetByIdAsync(_profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_profile);
        mods.Setup(repo => repo.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _mods);
        _settings = new SettingsService(_root);
        _scanner = new(profiles.Object, mods.Object, _settings);
    }

    private Mod AddMod(BslModRole role = BslModRole.None)
    {
        var mod = new Mod { Id = Guid.NewGuid(), Name = $"模组 {_mods.Count + 1}",
            Path = Path.Combine(_root, _mods.Count.ToString()), BslRole = role,
            BslDetectionVersion = BslModDetector.DetectionVersion };
        Directory.CreateDirectory(mod.Path);
        _profile.Mods.Add(new ProfileMod { ModId = mod.Id, Enabled = true, Order = _mods.Count });
        _mods.Add(mod);
        return mod;
    }

    [Fact]
    public async Task Scan_ShouldMatchResourceIdentityAcrossDifferentPatchNamesAndKeepUnsignedIds()
    {
        var first = AddMod();
        var second = AddMod();
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_0"), ulong.MaxValue);
        WritePatch(Path.Combine(second.Path, "bbbbbbbbbbbbbbbb.patch_8"), ulong.MaxValue, size: 60);
        // 伴随文件不能被当作额外 TOC 或资源持有者。
        File.WriteAllText(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_0.stream"), "not a TOC");
        var result = await _scanner.ScanProfileAsync(_profile.Id);
        var group = Assert.Single(result.Groups);
        Assert.Equal(ulong.MaxValue, group.ResourceId);
        Assert.Equal(Texture, group.TypeId);
        Assert.Equal(2, result.ScannedPatchCount);
        Assert.Equal(2, result.ScannedResourceCount);
        Assert.Equal(1, result.DefiniteConflictCount);
        Assert.Empty(result.Warnings);
        var view = new ConflictGroupViewModel(group);
        Assert.True(view.Owners.Last().IsWinner);
        Assert.Equal("部署顺序 #2", view.Owners.Last().OrderText);
    }

    [Fact]
    public async Task Scan_ShouldIgnoreSameFileNamesWithDifferentResourcesOrTypes()
    {
        var first = AddMod();
        var second = AddMod();
        const string file = "aaaaaaaaaaaaaaaa.patch_0";
        WritePatch(Path.Combine(first.Path, file), 10);
        WritePatch(Path.Combine(second.Path, file), 20);
        Assert.Empty((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        WritePatch(Path.Combine(second.Path, file), 10, type: Unit);
        Assert.Empty((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
    }

    [Fact]
    public async Task Scan_ShouldDistinguishUnitVersionAndTreatEqualMetadataAsPotential()
    {
        var first = AddMod();
        var second = AddMod();
        var a = Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_0");
        var b = Path.Combine(second.Path, "aaaaaaaaaaaaaaaa.patch_0");
        WritePatch(a, 1, type: Unit, version: 10800437);
        WritePatch(b, 1, type: Unit, version: 10800438);
        Assert.True(Assert.Single((await _scanner.ScanProfileAsync(_profile.Id)).Groups).IsDefiniteConflict);
        WritePatch(b, 1, type: Unit, version: 10800437);
        var group = Assert.Single((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        Assert.False(group.IsDefiniteConflict);
        Assert.Contains("内容仍可能不同", new ConflictGroupViewModel(group).StatusText);
    }

    [Fact]
    public async Task Scan_ShouldUseNumericPatchOrderReverseDirectionAndFixedRegion()
    {
        var first = AddMod();
        var second = AddMod();
        var boot = AddMod(BslModRole.SmoothBoot);
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_2"), 1, size: 60);
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_10"), 1);
        WritePatch(Path.Combine(second.Path, "aaaaaaaaaaaaaaaa.patch_0"), 1);
        // 重复资源不产生同一模组的自冲突，最终元数据取 patch_10。
        _profile.Mods[1].Enabled = false;
        Assert.Empty((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        _profile.Mods[1].Enabled = true;
        var group = Assert.Single((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        Assert.Equal(3, group.Owners.Count);
        Assert.EndsWith("patch_10", group.Owners[1].FullPath);
        Assert.False(group.IsDefiniteConflict);
        await _settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.BottomToTop.ToString());
        group = Assert.Single((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        Assert.Equal(first.Id, group.Owners.Last().ModId);
        Assert.EndsWith("patch_10", group.Owners.Last().FullPath);
        WritePatch(Path.Combine(boot.Path, "aaaaaaaaaaaaaaaa.patch_0"), 1);
        group = Assert.Single((await _scanner.ScanProfileAsync(_profile.Id)).Groups);
        Assert.Equal(boot.Id, group.Owners.Last().ModId);
        Assert.Equal("aaaaaaaaaaaaaaaa.patch_3", group.Owners.Last().TargetFileName);
    }

    [Fact]
    public async Task Scan_ShouldReportBrokenPatchesWithoutHidingReadableConflicts()
    {
        var first = AddMod();
        var second = AddMod();
        WritePatch(Path.Combine(first.Path, "aaaaaaaaaaaaaaaa.patch_0"), 1);
        WritePatch(Path.Combine(second.Path, "bbbbbbbbbbbbbbbb.patch_0"), 1);
        File.WriteAllText(Path.Combine(first.Path, "cccccccccccccccc.patch_0"), "broken");
        File.WriteAllBytes(Path.Combine(first.Path, "dddddddddddddddd.patch_0"), Array.Empty<byte>());
        var missing = AddMod();
        Directory.Delete(missing.Path);
        var result = await _scanner.ScanProfileAsync(_profile.Id);
        Assert.Single(result.Groups);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal(1, result.SkippedModCount);
        Assert.Contains(result.Warnings, warning => warning.ModName == first.Name && warning.FilePath.EndsWith("cccccccccccccccc.patch_0"));
    }

    [Fact]
    public async Task Scan_ShouldHonorCancellation()
    {
        AddMod();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanProfileAsync(_profile.Id, cts.Token));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("index")]
    [InlineData("offset")]
    [InlineData("size")]
    public void Reader_ShouldRejectInvalidBounds(string field)
    {
        var path = Path.Combine(_root, "invalid.patch_0");
        WritePatch(path, 1);
        var bytes = File.ReadAllBytes(path);
        switch (field)
        {
            case "magic": BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0); break;
            case "index": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue); break;
            case "offset": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104 + 16), ulong.MaxValue); break;
            case "size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104 + 56), uint.MaxValue); break;
        }
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => PatchResourceIndexReader.Read(path));
    }

    internal static void WritePatch(string path, ulong resourceId, ulong type = Texture, uint size = 48, uint version = 1)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0xF0000011u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(new byte[60]);
        writer.Write(new byte[32]);
        writer.Write(resourceId);
        writer.Write(type);
        writer.Write(184UL);
        writer.Write(new byte[32]);
        writer.Write(size);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(new byte[12]);
        var data = new byte[size];
        if (size >= 48) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), version);
        writer.Write(data);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
