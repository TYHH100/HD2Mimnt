using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
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

    private IasSystemService CreateService() => new(_modService, _settings);

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
        Assert.Equal(Path.Combine(systemDir, "Runtime", "abc123"), await service.GetRuntimeRootAsync());
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
    public async Task RuntimeRoot_RequiresLocationAndRuntimeDirectory()
    {
        var service = CreateService();
        Assert.Null(await service.GetRuntimeRootAsync());

        var location = Path.Combine(_root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));
        await service.SaveInstallLocationAsync(location);

        var root = await service.GetRuntimeRootAsync();
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"), root);

        // 保存的 Runtime ID 优先；不存在时回退第一个。
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-2"));
        await service.SaveActiveRuntimeIdAsync("rt-2");
        Assert.Equal("rt-2", await service.GetActiveRuntimeIdAsync());
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-2"),
            await service.GetRuntimeRootAsync());
        await service.SaveActiveRuntimeIdAsync("missing");
        Assert.Equal(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"),
            await service.GetRuntimeRootAsync());
    }

    [Fact]
    public async Task GetModelComponents_ListsRigFilesFromLibrary()
    {
        await ImportRigPack();
        var service = CreateService();

        var component = Assert.Single(await service.GetModelComponentsAsync());
        Assert.Equal("Hero", component.ModName);
        Assert.Equal("hero.rigbin", Assert.Single(component.Files).RelativePath);
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
