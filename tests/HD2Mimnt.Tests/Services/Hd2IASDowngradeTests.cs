using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Theory]
    [InlineData(Hd2IASRoute.Native, Hd2IASRoute.Native)]
    [InlineData(Hd2IASRoute.Native, Hd2IASRoute.Lua)]
    [InlineData(Hd2IASRoute.Lua, Hd2IASRoute.Native)]
    [InlineData(Hd2IASRoute.Lua, Hd2IASRoute.Lua)]
    public async Task Install_Downgrade_UsesExactManagedHashesAndUpdatesBuildAndSameDataMod(Hd2IASRoute from, Hd2IASRoute to)
    {
        var newer = CreatePackage(version: "1.4.5", includeBuild: true);
        var older = CreatePackage(version: "1.4.2", addonSuffix: "-old", includeBuild: true);
        var game = CreateGame("native"); var (service, store, _) = await InstallerAsync();
        var first = await service.InstallAsync(newer, game, from);
        var settings = Path.Combine(game, "bin", "HD2IndependentRig", "runtime.ini");
        await File.WriteAllTextAsync(settings, "keep-user-options");
        var inspection = await service.InspectAsync(older, game, to);
        Assert.Contains(inspection.FilePlan, file => file.Path == "bin/addon64" && file.Action == Hd2IASFileAction.ReplaceRecognized);
        var result = await service.InstallAsync(older, game, to);
        Assert.Equal(first.ModId, result.ModId);
        Assert.Equal("1.4.2", Assert.Single(await _iasMods.GetAllModsAsync()).Version);
        Assert.Equal((to == Hd2IASRoute.Native ? "native" : "lua") + "-addon-old",
            await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        var buildPath = Path.Combine(game, "bin", "HD2IndependentRig.build.json");
        Assert.Equal(BuildContents("1.4.2", to == Hd2IASRoute.Native ? "native" : "lua"), await File.ReadAllTextAsync(buildPath));
        var build = await service.ReadInstalledAsync(game);
        Assert.Equal("1.4.2", build.Version); Assert.Equal(to, build.Route);
        Assert.Contains((await store.ReadAsync(game))!.Files, file => file.Target == "bin/HD2IndependentRig.build.json");
        var timestamp = File.GetLastWriteTimeUtc(buildPath);
        await service.InstallAsync(older, game, to);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(buildPath));
        // 同一份活动所有权也支持再次升级，不依赖 incoming previous_sha256 的方向。
        await service.InstallAsync(newer, game, from);
        Assert.Equal("1.4.5", (await service.ReadInstalledAsync(game)).Version);
        Assert.Equal("keep-user-options", await File.ReadAllTextAsync(settings));
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
    }

    [Theory]
    [InlineData("bin/addon64")]
    [InlineData("bin/dxgi.dll")]
    [InlineData("bin/HD2IndependentRig.build.json")]
    public async Task Install_Downgrade_RejectsFilesModifiedSinceManagedInstallation(string target)
    {
        var newer = CreatePackage(version: "1.4.5", includeBuild: true);
        var older = CreatePackage(version: "1.4.2", addonSuffix: "-old", includeBuild: true);
        var game = CreateGame("native"); var (service, store, _) = await InstallerAsync();
        var first = await service.InstallAsync(newer, game, Hd2IASRoute.Native);
        var active = (await store.ReadAsync(game))!;
        await File.WriteAllTextAsync(Path.Combine(game, target), "user-modified");
        var report = await service.InspectAsync(older, game, Hd2IASRoute.Native);
        Assert.Contains(report.FilePlan, file => file.Path == target && file.Action == Hd2IASFileAction.BlockUnknown);
        await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(older, game, Hd2IASRoute.Native));
        Assert.Equal("user-modified", await File.ReadAllTextAsync(Path.Combine(game, target)));
        Assert.Equal(active.Revision, (await store.ReadAsync(game))!.Revision);
        Assert.Equal(first.ModId, Assert.Single(await _iasMods.GetAllModsAsync()).Id);
        Assert.Equal("1.4.5", Assert.Single(await _iasMods.GetAllModsAsync()).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_DowngradeFailureOrCancellation_RestoresNewerBuildAndRuntime(bool cancel)
    {
        var newer = CreatePackage(version: "1.4.5", includeBuild: true);
        var older = CreatePackage(version: "1.4.2", addonSuffix: "-old", includeBuild: true);
        var game = CreateGame("native"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(newer, game, Hd2IASRoute.Native);
        var active = (await store.ReadAsync(game))!;
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "retired.dll"), "old-retired");
        using var cancellation = new CancellationTokenSource(); var observed = false;
        var progress = new IASInstallProgress(message =>
        {
            if (message != "安装：bin/retired.dll") return;
            observed = true;
            Assert.Equal(BuildContents("1.4.2", "lua"), File.ReadAllText(Path.Combine(game, "bin", "HD2IndependentRig.build.json")));
            if (cancel) cancellation.Cancel(); else throw new IOException("simulated failure after build write");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(older, game, Hd2IASRoute.Lua, progress, cancellation.Token));
        Assert.True(observed);
        Assert.Equal(BuildContents("1.4.5", "native"), await File.ReadAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json")));
        Assert.Equal("native-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.Equal("old-retired", await File.ReadAllTextAsync(Path.Combine(game, "bin", "retired.dll")));
        Assert.Equal(active.Revision, (await store.ReadAsync(game))!.Revision);
        Assert.Equal("1.4.5", Assert.Single(await _iasMods.GetAllModsAsync()).Version);
        Assert.Empty(Directory.GetFiles(storage, "pending.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Install_LegacyUnownedBuild_UsesVerifiedCurrentPackageCacheInsteadOfUnclassifiedInventory(bool recognized)
    {
        var newer = CreatePackage(version: "1.4.5", includeBuild: true, previousBuildVersion: "1.4.4");
        var older = CreatePackage(version: "1.4.2", addonSuffix: "-old", includeBuild: true);
        var game = CreateGame("native"); var (service, store, storage) = await InstallerAsync();
        await service.InstallAsync(newer, game, Hd2IASRoute.Native);
        var buildPath = Path.Combine(game, "bin", "HD2IndependentRig.build.json");
        var contents = BuildContents(recognized ? "1.4.4" : "9.9.9", "native");
        await File.WriteAllTextAsync(buildPath, contents);
        var active = (await store.ReadAsync(game))!;
        active = active with
        {
            Files = active.Files.Where(file => file.Target != "bin/HD2IndependentRig.build.json").ToArray(),
            Inventory = [new("bin/HD2IndependentRig.build.json", Hash(contents), Hash(contents), "PreserveUnclassified")]
        };
        await File.WriteAllTextAsync(Path.Combine(Assert.Single(Directory.GetDirectories(storage)), "active.json"), JsonSerializer.Serialize(active));
        var cache = Path.Combine(_root, "update-cache"); Directory.CreateDirectory(cache);
        File.Copy(newer, Path.Combine(cache, "HD2IAS-1.4.5-update.zip"));
        if (recognized)
        {
            await service.InstallAsync(older, game, Hd2IASRoute.Native);
            Assert.Equal("1.4.2", (await service.ReadInstalledAsync(game)).Version);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(older, game, Hd2IASRoute.Native));
            Assert.Equal(contents, await File.ReadAllTextAsync(buildPath));
        }
    }

    [Theory]
    [InlineData("9.9.9", "native")]
    [InlineData("1.4.4", "lua")]
    public async Task ReadPackage_BuildMetadataDisagreesDespiteMatchingHash_Rejects(string version, string route)
    {
        var zip = CreatePackage(includeBuild: true);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var contents = BuildContents(version, route);
            var build = archive.GetEntry("RuntimePackage/Native/HD2IndependentRig.build.json")!; build.Delete();
            using (var writer = new StreamWriter(archive.CreateEntry("RuntimePackage/Native/HD2IndependentRig.build.json").Open()))
                await writer.WriteAsync(contents);
            var entry = archive.GetEntry("RuntimePackage/Native/package.json")!;
            JsonNode json;
            using (var input = entry.Open()) json = (await JsonNode.ParseAsync(input))!;
            json["files"]!.AsArray().Single(file => (string?)file!["path"] == "HD2IndependentRig.build.json")!["sha256"] = Hash(contents);
            entry.Delete();
            using var output = archive.CreateEntry("RuntimePackage/Native/package.json").Open();
            await JsonSerializer.SerializeAsync(output, json);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageManifestReader().ReadAsync(zip));
    }
}
