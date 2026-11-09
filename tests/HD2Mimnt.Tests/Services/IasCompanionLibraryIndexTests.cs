using System.IO.Compression;
using System.Text.Json.Nodes;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// IAS 配套库索引的 rig 收集：必须按部署记录里的选项快照取文件，
/// 不能用默认全量，否则配置页取消勾选的组件仍会进上游 armor 配套。
/// </summary>
public sealed class IasCompanionLibraryIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-ias-index-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly DeploymentRepository _deployments;
    private readonly ModImportService _import;
    private readonly DeploymentService _deployment;
    private readonly IasCompanionService _companion;

    public IasCompanionLibraryIndexTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new(_db); _profiles = new(_db); _deployments = new(_db);
        var library = Path.Combine(_root, "library");
        _import = new(_mods, library, tempDirectory: Path.Combine(_root, "temp"));
        var settings = new SettingsService(_root);
        var modService = new ModService(_mods, library);
        _deployment = new DeploymentService(_profiles, _mods, _deployments, [new CopyDeploymentMethod()]);
        _companion = new IasCompanionService(new IasSystemService(modService, settings), modService,
            _deployments, settings, Path.Combine(_root, "backup"), Path.Combine(_root, "cache"),
            Path.Combine(_root, "obsolete.json"));
    }

    [Fact]
    public async Task CollectsRigFilesFromDeployedOptionSnapshotNotDefaults()
    {
        var id = await ImportNestedPackage();
        var profile = await CreateProfile(id, [false, true]); // 只要模型补丁，rig 组件取消勾选
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var blobs = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(blobs); // 生产路径由 RegenerateAsync 建目录，这里单独调用需自建
        Assert.Equal(DeploymentStatus.Completed, await Deploy(profile, game));

        var row = Assert.Single(await _companion.BuildLibraryIndexAsync(game, blobs, CancellationToken.None));
        Assert.Empty(BundleFiles(row));

        // 勾选 rig 组件后重新部署，索引才带上该 rig 与对应 blob 快照。
        var stored = (await _profiles.GetByIdAsync(profile))!;
        stored.Mods[0].OptionsJson = new ProfileModOptionsState { OptionToggles = [true, true] }.ToJson();
        await _profiles.UpdateAsync(stored);
        Assert.Equal(DeploymentStatus.Completed, await Deploy(profile, game));

        row = Assert.Single(await _companion.BuildLibraryIndexAsync(game, blobs, CancellationToken.None));
        var file = Assert.Single(BundleFiles(row));
        var target = file["path"]!.GetValue<string>();
        Assert.Equal("bin/HD2IndependentRig/hero.rigbin", target);
        Assert.True(File.Exists(Path.Combine(blobs, file["sha256"]!.GetValue<string>())));
    }

    private static JsonArray BundleFiles(JsonObject row) => row["bundle"]!.AsObject()["files"]!.AsArray();

    private async Task<DeploymentStatus> Deploy(Guid profile, string game)
        => (await _deployment.DeployProfileAsync(profile, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, game)).Status;

    private async Task<Guid> ImportNestedPackage()
    {
        var rig = Path.Combine(_root, "rig-source", "bin", "HD2IndependentRig");
        Directory.CreateDirectory(rig);
        File.WriteAllText(Path.Combine(rig, "hero.rigbin"), "rig");
        var resources = Path.Combine(_root, "resources-source");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "0123456789abcdef.patch_9"), "patch");
        var outer = Path.Combine(_root, "outer");
        Directory.CreateDirectory(outer);
        ZipFile.CreateFromDirectory(Path.Combine(_root, "rig-source"), Path.Combine(outer, "Hero.hd2irpack.zip"));
        var inner = Path.Combine(_root, "inner");
        Directory.CreateDirectory(inner);
        ZipFile.CreateFromDirectory(resources, Path.Combine(inner, "Hero-model.zip"));
        ZipFile.CreateFromDirectory(inner, Path.Combine(outer, "Resources.zip"));
        var source = Path.Combine(_root, "Hero.zip");
        ZipFile.CreateFromDirectory(outer, source);
        return await _import.ImportModAsync(source, confirmHd2Ir: (_, _) => Task.CompletedTask);
    }

    private async Task<Guid> CreateProfile(Guid modId, List<bool> toggles)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = $"test-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
        profile.Mods.Add(new ProfileMod { ProfileId = profile.Id, ModId = modId, Enabled = true,
            OptionsJson = new ProfileModOptionsState { OptionToggles = toggles }.ToJson() });
        await _profiles.AddAsync(profile);
        return profile.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
