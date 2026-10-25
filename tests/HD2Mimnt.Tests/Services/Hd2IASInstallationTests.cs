using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Hd2IAS;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    private readonly List<HD2MimntDbContext> _iasDatabases = [];
    private ModService _iasMods = null!;
    private ProfileRepository _iasProfiles = null!;
    private ModRepository _iasModRepository = null!;
    private DeploymentRepository _iasRecords = null!;
    private DeploymentService _iasDeployment = null!;
    private async Task<(Hd2IASIntegrationService Service, Hd2IASDeploymentStore Store, string Storage)> InstallerAsync()
    {
        Directory.CreateDirectory(_root);
        var db = new HD2MimntDbContext(Path.Combine(_root, "ias-library.db"));
        _iasDatabases.Add(db); await db.InitializeDatabaseAsync();
        _iasProfiles = new ProfileRepository(db); _iasModRepository = new ModRepository(db); _iasRecords = new DeploymentRepository(db);
        _iasDeployment = new DeploymentService(_iasProfiles, _iasModRepository, _iasRecords, [new CopyDeploymentMethod()]);
        _iasMods = new ModService(_iasModRepository, Path.Combine(_root, "library"), deploymentService: _iasDeployment,
            tempDirectory: Path.Combine(_root, "temp"));
        var storage = Path.Combine(_root, "managed"); var store = new Hd2IASDeploymentStore(storage, Path.Combine(_root, "update-cache"));
        return (new(new PackageManifestReader(), new RuntimeCompatibilityChecker(store), new TestCompiler(),
            deploymentStore: store, deployments: _iasRecords, mods: _iasMods), store, storage);
    }

    [Theory]
    [InlineData(Hd2IASRoute.Native)]
    [InlineData(Hd2IASRoute.Lua)]
    public async Task Install_BothRoutes_DirectlyUsesInternalDataPreservesSettingsAndReusesIdenticalInstallation(Hd2IASRoute route)
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, storage) = await InstallerAsync();
        var ini = Path.Combine(game, "bin", "HD2IndependentRig", "runtime.ini");
        await File.WriteAllTextAsync(ini, "user-custom-options");
        var dxgi = Path.Combine(game, "bin", "dxgi.dll");
        var result = await service.InstallAsync(zip, game, route);
        var active = await store.ReadAsync(game);
        Assert.Equal(route, active!.Route); Assert.Equal("1.4.4", active.Version);
        Assert.Equal("user-custom-options", await File.ReadAllTextAsync(ini));
        Assert.Equal((route == Hd2IASRoute.Native ? "native" : "lua") + "-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.True(Directory.Exists(result.Path)); Assert.Empty(Directory.GetFiles(storage, "*.zip", SearchOption.AllDirectories));
        var deployed = Path.Combine(game, "data", StingrayArchiveWriter.PatchName);
        var internalPatch = Path.Combine(result.Path, "data", StingrayArchiveWriter.PatchName);
        Assert.False(File.Exists(deployed));
        Assert.NotEmpty(StingrayArchiveWriter.ReadSource(await File.ReadAllBytesAsync(internalPatch), 0x70217982548cf034));
        var dataMod = Assert.Single(await _iasMods.GetAllModsAsync());
        Assert.Equal(result.ModId, dataMod.Id); Assert.Equal("1.4.4", dataMod.Version);
        var modified = File.GetLastWriteTimeUtc(dxgi);
        var repeated = await service.InstallAsync(zip, game, route);
        Assert.Equal(result.Path, repeated.Path); Assert.Equal(result.ModId, repeated.ModId); Assert.False(File.Exists(deployed));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(dxgi));
        Assert.Single(Directory.GetDirectories(Path.Combine(Assert.Single(Directory.GetDirectories(storage)), "versions")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_Update_OverwritesChangedFilesWithoutUninstallAndKeepsInventory(bool cancelBeforeWrite)
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, _) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Lua);
        var before = (await store.ReadAsync(game))!;
        var config = Path.Combine(game, "bin", "HD2IndependentRig", "free_parts_selections.ini");
        var unknown = Path.Combine(game, "bin", "HD2IndependentRig", "user-note.txt");
        await File.WriteAllTextAsync(config, "custom selection"); await File.WriteAllTextAsync(unknown, "user-owned");
        var missing = Path.Combine(game, "bin", "addon64"); File.Delete(missing);
        var boundaryObserved = false; using var cancellation = new CancellationTokenSource();
        var progress = new IASInstallProgress(message =>
        {
            if (message != "安装：bin/addon64") return;
            boundaryObserved = true;
            Assert.False(File.Exists(missing));
            Assert.True(File.Exists(Path.Combine(game, "bin", "dxgi.dll"))); // 覆盖更新不预先卸载相同文件。
            Assert.False(File.Exists(Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
            Assert.Equal("custom selection", File.ReadAllText(config)); Assert.Equal("user-owned", File.ReadAllText(unknown));
            if (cancelBeforeWrite) cancellation.Cancel();
        });
        if (cancelBeforeWrite)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(zip, game, Hd2IASRoute.Native, progress, cancellation.Token));
            Assert.Equal(before.Revision, (await store.ReadAsync(game))!.Revision);
            Assert.False(File.Exists(missing)); // 回滚到开始操作前的缺失状态。
            Assert.True(File.Exists(Path.Combine(game, "bin", "dxgi.dll")));
            Assert.False(File.Exists(Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
        }
        else
        {
            await service.InstallAsync(zip, game, Hd2IASRoute.Native, progress, cancellation.Token);
            var after = (await store.ReadAsync(game))!;
            Assert.Equal(Hd2IASRoute.Native, after.Route);
            Assert.Contains(after.Inventory, item => item.Target == "bin/addon64" && item.Disposition == "InstallMissing");
            Assert.Contains(after.Inventory, item => item.Target.EndsWith("free_parts_selections.ini") && item.Disposition == "PreserveUserData");
            Assert.Contains(after.Inventory, item => item.Target.EndsWith("user-note.txt") && item.Disposition == "PreserveUnclassified");
            Assert.Contains(after.Inventory, item => item.Target == "bin/dxgi.dll" && item.Disposition == "HashMatches");
        }
        Assert.True(boundaryObserved);
        Assert.Equal("custom selection", await File.ReadAllTextAsync(config)); Assert.Equal("user-owned", await File.ReadAllTextAsync(unknown));
    }

    [Fact]
    public async Task Install_SwitchBothDirections_ReplacesRuntimeAndDataAsOneSystem()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, _) = await InstallerAsync();
        byte[]? native = null;
        foreach (var route in new[] { Hd2IASRoute.Native, Hd2IASRoute.Lua, Hd2IASRoute.Native })
        {
            var generated = await service.InstallAsync(zip, game, route);
            var active = (await store.ReadAsync(game))!;
            Assert.Equal(route, active.Route);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(generated.Path, "data", StingrayArchiveWriter.PatchName));
            if (route == Hd2IASRoute.Native) { if (native != null) Assert.Equal(native, bytes); native = bytes; }
            else Assert.NotEqual(native, bytes);
            Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
            Assert.Single(await _iasMods.GetAllModsAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_FailureOrCancellationDuringWrite_RestoresRealOldFilesAndPreviousState(bool cancel)
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Lua);
        var before = (await store.ReadAsync(game))!;
        var originalMod = Assert.Single(await _iasMods.GetAllModsAsync());
        var patch = await File.ReadAllBytesAsync(Path.Combine(originalMod.Path, "data", StingrayArchiveWriter.PatchName));
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "retired.dll"), "old-retired");
        using var cancellation = new CancellationTokenSource(); var writes = 0;
        var progress = new IASInstallProgress(message =>
        {
            if (!message.StartsWith("安装：") || ++writes < 2) return;
            if (cancel) cancellation.Cancel(); else throw new IOException("simulated write failure");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(zip, game, Hd2IASRoute.Native, progress, cancellation.Token));
        Assert.True(writes >= 2);
        Assert.Equal("lua-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Equal(patch, await File.ReadAllBytesAsync(Path.Combine(originalMod.Path, "data", StingrayArchiveWriter.PatchName)));
        Assert.Equal(before.Revision, (await store.ReadAsync(game))!.Revision);
        Assert.Empty(Directory.GetFiles(storage, "pending.json", SearchOption.AllDirectories));
        Assert.Single(Directory.GetDirectories(Path.Combine(Assert.Single(Directory.GetDirectories(storage)), "versions")));
    }

    [Theory]
    [InlineData("bin/dxgi.dll", "reshade")]
    [InlineData("bin/helldivers2.exe", "new-game")]
    [InlineData("bin/addon64", "unknown-addon")]
    public async Task Install_UnknownInjectorGameOrTarget_BlocksWithoutMutatingGame(string relative, string contents)
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, storage) = await InstallerAsync();
        var target = Path.Combine(game, relative); await File.WriteAllTextAsync(target, contents);
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(zip, game, Hd2IASRoute.Native));
        Assert.Equal(contents, await File.ReadAllTextAsync(target));
        if (relative != "bin/addon64") Assert.Equal("lua-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Null(await store.ReadAsync(game));
        if (Directory.Exists(storage)) Assert.Empty(Directory.GetFiles(storage, "pending.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Install_InterruptedWrite_RestoresBackupBeforeNextOperation()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Lua);
        var previous = (await store.ReadAsync(game))!; var root = Assert.Single(Directory.GetDirectories(storage));
        var revision = Guid.NewGuid().ToString("N"); var directory = Path.Combine(root, "versions", revision);
        var backup = Path.Combine(directory, "before", "bin", "addon64"); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(backup, "lua-addon");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "addon64"), "partial-copy");
        await File.WriteAllTextAsync(Path.Combine(root, "pending.json"), JsonSerializer.Serialize(new Hd2IASDeploymentStore.Journal(revision,
            [new("bin/addon64", Hash("lua-addon"), Hash("native-addon"))], 1)));
        await store.RecoverAsync(game);
        Assert.Equal("lua-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Equal(previous.Revision, (await store.ReadAsync(game))!.Revision);
        Assert.False(Directory.Exists(directory)); Assert.False(File.Exists(Path.Combine(root, "pending.json")));
    }

    [Fact]
    public async Task Install_DataUsesNormalProfileDeploymentAndUpdatesSameEntryPreservingSelection()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, _, _) = await InstallerAsync();
        var generated = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var dataId = generated.ModId!.Value;
        var systemBytes = await File.ReadAllBytesAsync(Path.Combine(generated.Path, "data", StingrayArchiveWriter.PatchName));
        var profile = new Profile { Id = Guid.NewGuid(), Name = "IAS profile" };
        profile.Mods.Add(new() { ModId = dataId, ProfileId = profile.Id, Enabled = true, Order = 0 });
        foreach (var (name, role, resource) in new[] { ("ordinary", BslModRole.None, 1UL), ("loader", BslModRole.MdlLoader, 2UL) })
        {
            var folder = Path.Combine(_root, name); Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, StingrayArchiveWriter.PatchName), StingrayArchiveWriter.Build([1, 2, 3], resource));
            var mod = new Mod { Id = Guid.NewGuid(), Name = name, Path = folder, BslRole = role, BslDetectionVersion = int.MaxValue };
            await _iasModRepository.AddAsync(mod); profile.Mods.Add(new() { ModId = mod.Id, ProfileId = profile.Id, Enabled = true, Order = profile.Mods.Count });
        }
        await _iasProfiles.AddAsync(profile);
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var result = await _iasDeployment.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);
            Assert.True(result.Status == DeploymentStatus.Completed, result.ErrorMessage);
            Assert.Equal(systemBytes, await File.ReadAllBytesAsync(Path.Combine(game, "data", "9ba626afa44a3aa3.patch_1")));
        }
        await _iasDeployment.DeleteModDeploymentRecordsAsync(profile.Mods[1].ModId);
        Assert.Equal(systemBytes, await File.ReadAllBytesAsync(Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
        profile.Mods[0].Enabled = false; await _iasProfiles.UpdateAsync(profile);
        var updated = await service.InstallAsync(zip, game, Hd2IASRoute.Lua);
        Assert.Equal(dataId, updated.ModId);
        Assert.Equal(3, (await _iasMods.GetAllModsAsync()).Count());
        var preserved = (await _iasProfiles.GetByIdAsync(profile.Id))!.Mods.Single(item => item.ModId == dataId);
        Assert.False(preserved.Enabled); Assert.Equal(0, preserved.Order);
        // 更新不删除部署记录；模组在配置中已停用，自动部署跳过，游戏里的旧 data 保留到下次手动部署。
        var preservedDeployment = Assert.Single(await _iasDeployment.GetModDeploymentHistoryAsync(dataId));
        Assert.Equal(systemBytes, await File.ReadAllBytesAsync(
            Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
        Assert.NotEmpty(preservedDeployment.Files);
        var updatedBytes = await File.ReadAllBytesAsync(Path.Combine(updated.Path, "data", StingrayArchiveWriter.PatchName));
        Assert.NotEqual(systemBytes, updatedBytes);
        var gamePatches = Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*");
        Assert.Equal(systemBytes, await File.ReadAllBytesAsync(
            Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
        Assert.DoesNotContain(gamePatches, path => File.ReadAllBytes(path).SequenceEqual(updatedBytes));
    }

    [Fact]
    public async Task Install_PreviouslyImportedDataWithoutMarker_UpdatesExistingModAndLeavesGameDataAlone()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, _, _) = await InstallerAsync();
        var legacy = Path.Combine(_root, "legacy");
        var manifest = (await new PackageManifestReader().ReadAsync(zip)).Lua;
        var body = System.Text.Encoding.UTF8.GetBytes("local route = \"lua\"");
        var source = IndependentLuaSourceBuilder.Build(body, System.Text.Encoding.UTF8.GetBytes(Template), manifest);
        await Hd2IASDataDirectoryWriter.WriteAsync(legacy, manifest, source, default);
        File.Delete(Path.Combine(legacy, Hd2IASDataDirectoryWriter.IdentityFile));
        var json = await HD2Mimnt.Core.Services.ManifestPackage.ReadAsync(Path.Combine(legacy, "manifest.json"));
        json["Guid"] = Guid.NewGuid().ToString();
        await HD2Mimnt.Core.Services.ManifestPackage.SaveAsync(Path.Combine(legacy, "manifest.json"), json);
        var id = await _iasMods.ImportModAsync(legacy);
        await File.WriteAllTextAsync(Path.Combine(game, "data", "9ba626afa44a3aa3.patch_0.stream"), "leave-existing-game-data");
        var updated = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        Assert.Equal(id, updated.ModId); Assert.Single(await _iasMods.GetAllModsAsync());
        Assert.Equal("leave-existing-game-data", await File.ReadAllTextAsync(Path.Combine(game, "data", "9ba626afa44a3aa3.patch_0.stream")));
        Assert.False(File.Exists(Path.Combine(game, "data", StingrayArchiveWriter.PatchName)));
    }

    [Fact]
    public async Task Install_NewerPackage_UpdatesExistingVersionAndPreservesProfileOptions()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, _, _) = await InstallerAsync();
        var installed = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "keep selection" };
        profile.Mods.Add(new() { ProfileId = profile.Id, ModId = installed.ModId!.Value, Enabled = false,
            Order = 7, OptionsJson = "{\"custom\":true}" });
        await _iasProfiles.AddAsync(profile);
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
            foreach (var name in new[] { "RuntimePackage/package.json", "RuntimePackage/Native/package.json", "app/update_notes.json" })
            {
                var entry = archive.GetEntry(name)!;
                System.Text.Json.Nodes.JsonNode json;
                using (var stream = entry.Open()) json = (await System.Text.Json.Nodes.JsonNode.ParseAsync(stream))!;
                json["version"] = "1.4.5"; entry.Delete();
                using var destination = archive.CreateEntry(name).Open();
                await JsonSerializer.SerializeAsync(destination, json);
            }
        var updated = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var mod = Assert.Single(await _iasMods.GetAllModsAsync());
        Assert.Equal(installed.ModId, updated.ModId); Assert.Equal("1.4.5", mod.Version);
        var member = Assert.Single((await _iasProfiles.GetByIdAsync(profile.Id))!.Mods);
        Assert.False(member.Enabled); Assert.Equal(7, member.Order); Assert.Equal("{\"custom\":true}", member.OptionsJson);
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
    }

    [Fact]
    public async Task Install_UnrelatedModWithSameName_DoesNotReplaceIt()
    {
        var zip = CreatePackage(); var game = CreateGame("lua"); var (service, _, _) = await InstallerAsync();
        var path = Path.Combine(_root, "unrelated"); Directory.CreateDirectory(path);
        var bytes = StingrayArchiveWriter.Build([1, 2, 3], 1234);
        await File.WriteAllBytesAsync(Path.Combine(path, StingrayArchiveWriter.PatchName), bytes);
        var original = new Mod { Id = Guid.NewGuid(), Name = "HD2IAS data", Path = path, Version = "custom" };
        await _iasModRepository.AddAsync(original);
        var installed = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        Assert.NotEqual(original.Id, installed.ModId);
        Assert.Equal(2, (await _iasMods.GetAllModsAsync()).Count());
        Assert.Equal("custom", (await _iasMods.GetModByIdAsync(original.Id))!.Version);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(path, StingrayArchiveWriter.PatchName)));
    }

    private sealed class IASInstallProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
