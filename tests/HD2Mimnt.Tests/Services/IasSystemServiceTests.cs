using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class IasSystemServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-ias-system-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ModImportService _import;
    private readonly SettingsService _settings;
    private readonly ModService _modService;

    public IasSystemServiceTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new(_db);
        _import = new(_mods, Path.Combine(_root, "library"), tempDirectory: Path.Combine(_root, "temp"));
        _settings = new(_root);
        _modService = new ModService(_mods, Path.Combine(_root, "library"));
    }

    private IasSystemService CreateService() => new(_modService, _settings,
        [new CopyDeploymentMethod(), new HardLinkDeploymentMethod()],
        Path.Combine(_root, "IAS-System"), Path.Combine(_root, "library"));

    [Theory]
    [InlineData("C:\\src", "C:\\tgt", DeploymentMethod.HardLink)]
    [InlineData("C:\\src", "D:\\tgt", DeploymentMethod.Copy)]
    [InlineData("C:\\src", "C:\\tgt\\sub", DeploymentMethod.HardLink)]
    public void ResolveMethod_RequiresSameVolumeForHardLink(string source, string target, DeploymentMethod expected)
        => Assert.Equal(expected, IasSystemService.ResolveMethod(DeploymentMethod.HardLink, source, target));

    [Fact]
    public void ResolveMethod_KeepsNonHardLinkMethods()
    {
        Assert.Equal(DeploymentMethod.Copy, IasSystemService.ResolveMethod(DeploymentMethod.Copy, "C:\\a", "D:\\b"));
        Assert.Equal(DeploymentMethod.SymbolicLink, IasSystemService.ResolveMethod(DeploymentMethod.SymbolicLink, "C:\\a", "D:\\b"));
    }

    [Fact]
    public async Task DiscoverRuntimeIds_FindsExistingDirectoriesOnly()
    {
        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "abc123"));
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "def456"));
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "abc123", "nested"));
        var service = CreateService();

        Assert.Equal(["abc123", "def456"], await service.DiscoverRuntimeIdsAsync(location));
        Assert.Empty(await service.DiscoverRuntimeIdsAsync(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task DiscoverRuntimeIds_AcceptsSystemDirectorySelectedDirectly()
    {
        // 用户直接选中 HD2IntegratedArmorSystem 目录本身（而非其父目录）时也要能识别 Runtime。
        var systemDir = Path.Combine(_root, "ias", "HD2IntegratedArmorSystem");
        Directory.CreateDirectory(Path.Combine(systemDir, "Runtime", "abc123"));
        Directory.CreateDirectory(Path.Combine(systemDir, "Runtime", "def456"));
        var service = CreateService();
        await service.SaveInstallLocationAsync(systemDir);

        Assert.Equal(["abc123", "def456"], await service.DiscoverRuntimeIdsAsync(systemDir));
        Assert.Equal(Path.Combine(systemDir, "Runtime", "abc123", "armor", "rig"),
            await service.GetRigTargetRootAsync());
    }

    [Fact]
    public async Task InstallLocation_RoundTrips()
    {
        var service = CreateService();
        Assert.Null(await service.GetInstallLocationAsync());
        await service.SaveInstallLocationAsync(Path.Combine(_root, "ias"));
        Assert.Equal(Path.Combine(_root, "ias"), await service.GetInstallLocationAsync());
    }

    [Fact]
    public async Task Deploy_CopiesComponentsToRig_WritesManifest_AndUninstallRemoves()
    {
        await ImportRigPack();
        var service = CreateService();
        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));

        var result = await service.DeployAsync(location, "rt-1");

        Assert.Equal("Copy", result.UsedMethod);
        Assert.False(result.UsedCopyFallback);
        Assert.EndsWith(Path.Combine("armor", "rig"), result.TargetRoot);
        var rigFile = Path.Combine(result.TargetRoot, "hero.rigbin");
        Assert.Equal("rig", await File.ReadAllTextAsync(rigFile));
        Assert.True(await service.HasDeploymentAsync());

        var component = Assert.Single(await service.GetModelComponentsAsync());
        Assert.Equal("Hero", component.ModName);
        Assert.Equal("hero.rigbin", Assert.Single(component.Files).RelativePath);

        var uninstall = await service.UninstallAsync();
        Assert.Equal(1, uninstall.RemovedCount);
        Assert.Empty(uninstall.MissingFiles);
        Assert.False(File.Exists(rigFile));
        Assert.False(await service.HasDeploymentAsync());
        Assert.False(Directory.Exists(result.TargetRoot));
    }

    [Fact]
    public async Task Deploy_HardLinkOnSameDrive_IsIncremental()
    {
        await ImportRigPack();
        var service = CreateService();
        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));
        await _settings.SetSettingsAsync(new Dictionary<string, object> { [SettingKeys.DefaultDeploymentMethod] = "HardLink" });

        var first = await service.DeployAsync(location, "rt-1");
        Assert.Equal("HardLink", first.UsedMethod);
        Assert.Equal(1, first.DeployedCount);

        // 已是最新时整体跳过（增量），重复部署不重写文件。
        var second = await service.DeployAsync(location, "rt-1");
        Assert.Equal(0, second.DeployedCount);
        Assert.Equal(1, second.SkippedCount);
    }

    [Fact]
    public async Task Deploy_WithoutComponents_Throws()
    {
        var service = CreateService();
        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeployAsync(location, "rt-1"));
    }

    [Fact]
    public async Task RigTargetRoot_RequiresLocationAndRuntimeDirectory()
    {
        var service = CreateService();
        Assert.Null(await service.GetRigTargetRootAsync());

        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));
        await service.SaveInstallLocationAsync(location);

        var root = await service.GetRigTargetRootAsync();
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1", "armor", "rig"), root);

        // 保存的 Runtime ID 优先；不存在时回退第一个。
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-2"));
        await service.SaveActiveRuntimeIdAsync("rt-2");
        Assert.Equal("rt-2", await service.GetActiveRuntimeIdAsync());
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-2", "armor", "rig"),
            await service.GetRigTargetRootAsync());
        await service.SaveActiveRuntimeIdAsync("missing");
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1", "armor", "rig"),
            await service.GetRigTargetRootAsync());
    }

    [Fact]
    public async Task Deploy_WithMissingRuntimeDirectory_Throws()
    {
        await ImportRigPack();
        var service = CreateService();
        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(location);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.DeployAsync(location, "rt-1"));
    }

    private async Task<Guid> ImportRigPack()
    {
        var directory = Path.Combine(_root, "rig");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "hero.rigbin"), "rig");
        var archive = Path.Combine(_root, "Hero.hd2irpack.zip");
        ZipFile.CreateFromDirectory(directory, archive);
        return await _import.ImportModAsync(archive);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
