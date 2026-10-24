using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Theory]
    [InlineData(Hd2IASRoute.Native, false)]
    [InlineData(Hd2IASRoute.Native, true)]
    [InlineData(Hd2IASRoute.Lua, true)]
    public async Task Uninstall_RemovesOnlyRecordedUnchangedRuntimeAndAlwaysKeepsDxgiSettingsAndData(Hd2IASRoute route, bool changedDxgi)
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("lua");
        var (service, store, _) = await InstallerAsync();
        var data = await service.InstallAsync(zip, game, route);
        var ini = Path.Combine(game, "bin", "HD2IndependentRig", "runtime.ini");
        var unknown = Path.Combine(game, "bin", "HD2IndependentRig", "my-note.txt");
        var dxgi = Path.Combine(game, "bin", "dxgi.dll");
        await File.WriteAllTextAsync(ini, "keep-user-settings"); await File.WriteAllTextAsync(unknown, "keep-note");
        if (changedDxgi) await File.WriteAllTextAsync(dxgi, "external-reshade");
        var patch = Path.Combine(game, "data", StingrayArchiveWriter.PatchName);
        await File.WriteAllTextAsync(patch, "ordinary-profile-deployment");
        Assert.False((await service.GetManagementStateAsync(game)).NeedsInstallation);
        var result = await service.UninstallAsync(game);
        Assert.Contains("bin/addon64", result.RemovedFiles);
        Assert.Contains("bin/HD2IndependentRig.build.json", result.RemovedFiles);
        Assert.DoesNotContain("bin/dxgi.dll", result.RemovedFiles); Assert.Empty(result.ModifiedFiles);
        Assert.Equal(changedDxgi ? "external-reshade" : "dxgi", await File.ReadAllTextAsync(dxgi));
        Assert.Equal("keep-user-settings", await File.ReadAllTextAsync(ini)); Assert.Equal("keep-note", await File.ReadAllTextAsync(unknown));
        Assert.Equal("ordinary-profile-deployment", await File.ReadAllTextAsync(patch));
        Assert.Equal(data.ModId, Assert.Single(await _iasMods.GetAllModsAsync()).Id);
        Assert.Null(await store.ReadAsync(game));
        var state = await service.GetManagementStateAsync(game);
        Assert.True(state.HasDataMod); Assert.True(state.NeedsInstallation); Assert.False(state.HasManagedInstallation);
        if (!changedDxgi)
        {
            var reinstalled = await service.InstallAsync(zip, game, route);
            Assert.Equal(data.ModId, reinstalled.ModId); Assert.False((await service.GetManagementStateAsync(game)).NeedsInstallation);
        }
    }

    [Fact]
    public async Task Uninstall_ModifiedOwnedFileIsPreservedAndReportedWithoutRemovingUnrelatedFiles()
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native"); var (service, _, _) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var addon = Path.Combine(game, "bin", "addon64"); await File.WriteAllTextAsync(addon, "external-replacement");
        var result = await service.UninstallAsync(game);
        Assert.Equal("bin/addon64", Assert.Single(result.ModifiedFiles));
        Assert.Equal("external-replacement", await File.ReadAllTextAsync(addon));
        Assert.True(File.Exists(Path.Combine(game, "bin", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game, "bin", "helldivers2.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_FailureOrCancellationAfterFirstDelete_RestoresFilesAndOwnership(bool cancel)
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var before = (await store.ReadAsync(game))!; var observed = false;
        using var cancellation = new CancellationTokenSource();
        var progress = new IASInstallProgress(message =>
        {
            if (message != "卸载：bin/HD2IndependentRig.build.json") return;
            observed = true; Assert.False(File.Exists(Path.Combine(game, "bin", "addon64")));
            if (cancel) cancellation.Cancel(); else throw new IOException("simulated uninstall failure");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => service.UninstallAsync(game, progress, cancellation.Token));
        Assert.True(observed); Assert.Equal(before.Revision, (await store.ReadAsync(game))!.Revision);
        Assert.Equal("native-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Equal(BuildContents("1.4.4", "native"), await File.ReadAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json")));
        Assert.Empty(Directory.GetFiles(storage, "pending.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Uninstall_CommittedTombstonePreventsRecoveryFromRestoringRemovedRuntime()
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        await service.UninstallAsync(game);
        var root = Assert.Single(Directory.GetDirectories(storage));
        var state = JsonSerializer.Deserialize<Hd2IASDeploymentStore.Installation>(await File.ReadAllTextAsync(Path.Combine(root, "active.json")))!;
        Assert.True(state.IsUninstalled);
        await File.WriteAllTextAsync(Path.Combine(root, "pending.json"), JsonSerializer.Serialize(new Hd2IASDeploymentStore.Journal(state.Revision,
            [new("bin/addon64", Hash("native-addon"), null)], 1)));
        await store.RecoverAsync(game);
        Assert.False(File.Exists(Path.Combine(game, "bin", "addon64"))); Assert.Null(await store.ReadAsync(game));
        Assert.False(File.Exists(Path.Combine(root, "pending.json")));
    }

    [Fact]
    public async Task Uninstall_NoManagerRecord_DoesNotGuessByFileName()
    {
        var game = CreateGame("native"); var (service, _, _) = await InstallerAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(game));
        Assert.Equal("native-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Equal("dxgi", await File.ReadAllTextAsync(Path.Combine(game, "bin", "dxgi.dll")));
    }

    [Fact]
    public async Task RuntimeRequirement_RecognizesLegacyDataAndDoesNotTreatVersionJsonAsWorkingRuntime()
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native"); var (service, _, _) = await InstallerAsync();
        var exported = await service.GenerateAsync(zip, game, Hd2IASRoute.Native, Path.Combine(_root, "old-manual-data.zip"));
        await _iasMods.ImportModAsync(exported.Path);
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json"), BuildContents("1.4.4", "native"));
        var missing = await service.GetManagementStateAsync(game);
        Assert.True(missing.HasDataMod); Assert.True(missing.NeedsInstallation);
        Assert.Contains("bin/HD2IndependentRig.addon64", missing.MissingFiles);
        Assert.Equal("1.4.4", missing.Build.Version);
        Assert.False((await service.GetManagementStateAsync(null)).NeedsInstallation);
    }

    [Fact]
    public async Task RuntimeRequirement_UnrelatedSameNameDoesNotPromptAndManagedMissingFileDoes()
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native"); var (service, _, _) = await InstallerAsync();
        await _iasModRepository.AddAsync(new Mod { Id = Guid.NewGuid(), Name = "HD2IAS data", Path = Path.Combine(_root, "unrelated") });
        Assert.False((await service.GetManagementStateAsync(game)).NeedsInstallation);
        await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        Assert.False((await service.GetManagementStateAsync(game)).NeedsInstallation);
        File.Delete(Path.Combine(game, "bin", "addon64"));
        var state = await service.GetManagementStateAsync(game);
        Assert.True(state.NeedsInstallation); Assert.True(state.HasManagedInstallation);
        Assert.Equal("bin/addon64", Assert.Single(state.MissingFiles));
    }
}
