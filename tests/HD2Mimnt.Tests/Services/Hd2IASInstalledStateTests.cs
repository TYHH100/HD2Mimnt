using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Theory]
    [InlineData("native", Hd2IASRoute.Native)]
    [InlineData("lua", Hd2IASRoute.Lua)]
    public async Task ReadInstalled_WithoutUpdatePackage_ReadsVersionRouteAndFullContents(string route, Hd2IASRoute expected)
    {
        var game = CreateGame("native");
        var path = Path.Combine(game, "bin", "HD2IndependentRig.build.json");
        var contents = JsonSerializer.Serialize(new { schema = "HD2IndependentRigBuild1", version = "1.4.3", pose_route = route,
            capabilities = new[] { "test-capability" } });
        await File.WriteAllTextAsync(path, contents);
        var service = new Hd2IASIntegrationService(new PackageManifestReader(), new RuntimeCompatibilityChecker());
        var result = await service.ReadInstalledAsync(game);
        Assert.Equal(Hd2IASBuildState.Read, result.State); Assert.Equal("1.4.3", result.Version);
        Assert.Equal(expected, result.Route); Assert.Equal(contents, result.Contents);
        Assert.Equal(path, result.Path);
        Assert.Contains(result.ConfigurationFiles, file => file.Path.EndsWith("runtime.ini") && file.Kind == Hd2IASConfigKind.UserSettings);
    }

    [Theory]
    [InlineData("{invalid json")]
    [InlineData("{\"schema\":\"HD2IndependentRigBuild99\",\"version\":\"1.4.4\"}")]
    [InlineData("{\"schema\":\"HD2IndependentRigBuild1\"}")]
    [InlineData("{\"schema\":\"HD2IndependentRigBuild1\",\"version\":\"wrong\"}")]
    public async Task ReadInstalled_InvalidBuildKeepsRawContentsAndDoesNotClaimVersion(string contents)
    {
        var game = CreateGame("native");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json"), contents);
        var result = await new RuntimeCompatibilityChecker().ReadInstalledAsync(game);
        Assert.Equal(Hd2IASBuildState.Invalid, result.State); Assert.Null(result.Version);
        Assert.Equal(contents, result.Contents);
        Assert.NotEmpty(result.ConfigurationFiles);
    }

    [Fact]
    public async Task ReadInstalled_MissingOrUnconfigured_DoesNotInferVersion()
    {
        var inspector = new RuntimeCompatibilityChecker();
        Assert.Equal(Hd2IASBuildState.NotChecked, (await inspector.ReadInstalledAsync(null)).State);
        var result = await inspector.ReadInstalledAsync(CreateGame("native"));
        Assert.Equal(Hd2IASBuildState.Missing, result.State); Assert.Null(result.Version);
    }

    [Fact]
    public async Task Inspect_PreservesUserIniAndSkipsIdenticalFilesButStillChecksSystemCatalogs()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage());
        var game = CreateGame("native");
        var directory = Path.Combine(game, "bin", "HD2IndependentRig");
        var settings = new Dictionary<string, string>
        {
            ["runtime.ini"] = "helmet_selector_enabled=1\nuser_option=keep",
            ["free_parts_selections.ini"] = "HD2FreePartsSelections1\nmy-choice",
            ["physics_control.ini"] = "HD2PhysicsControl1\nenabled=0",
            ["custom.ini"] = "unknown-user-setting=keep"
        };
        foreach (var file in settings) await File.WriteAllTextAsync(Path.Combine(directory, file.Key), file.Value);
        await File.WriteAllTextAsync(Path.Combine(directory, "helmet_manifest.ini"), "helmet");
        await File.WriteAllTextAsync(Path.Combine(directory, "transmog_manifest.ini"), "old-transmog");
        package = package with { Foundation = package.Foundation with { Files = package.Foundation.Files.Concat(new[]
        {
            new Hd2IASFile("bin/HD2IndependentRig/helmet_manifest.ini", "bin/HD2IndependentRig/helmet_manifest.ini", Hash("helmet"), null, []),
            new Hd2IASFile("bin/HD2IndependentRig/transmog_manifest.ini", "bin/HD2IndependentRig/transmog_manifest.ini", Hash("transmog"), null, [Hash("old-transmog")])
        }).ToArray() } };
        var report = await new RuntimeCompatibilityChecker().InspectAsync(package, game, Hd2IASRoute.Native);
        foreach (var file in settings)
        {
            var plan = Assert.Single(report.FilePlan, plan => plan.Path == "bin/HD2IndependentRig/" + file.Key);
            Assert.Equal(Hd2IASFileAction.PreserveSettings, plan.Action);
            Assert.Equal(file.Value, await File.ReadAllTextAsync(Path.Combine(directory, file.Key)));
        }
        Assert.Contains(report.FilePlan, plan => plan.Path == "bin/addon64" && plan.Action == Hd2IASFileAction.SkipUnchanged &&
            plan.ActualSha256 == Hash("native-addon") && plan.ActualSha256 == plan.ExpectedSha256);
        Assert.Contains(report.FilePlan, plan => plan.Path.EndsWith("helmet_manifest.ini") && plan.Action == Hd2IASFileAction.SkipUnchanged);
        Assert.Contains(report.FilePlan, plan => plan.Path.EndsWith("transmog_manifest.ini") && plan.Action == Hd2IASFileAction.ReplaceRecognized);
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "dxgi.dll"), "reshade-unknown");
        report = await new RuntimeCompatibilityChecker().InspectAsync(package, game, Hd2IASRoute.Native);
        Assert.Contains(report.FilePlan, plan => plan.Path == "bin/dxgi.dll" && plan.Action == Hd2IASFileAction.BlockUnknown);
        Assert.Equal("reshade-unknown", await File.ReadAllTextAsync(Path.Combine(game, "bin", "dxgi.dll")));
    }

    [Fact]
    public async Task Inspect_BuildVersionDeclarationDoesNotReplaceRuntimeHashVerification()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage());
        var game = CreateGame("native");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json"),
            "{\"schema\":\"HD2IndependentRigBuild1\",\"version\":\"9.9.9\",\"pose_route\":\"lua\"}");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "addon64"), "changed-addon");
        var report = await new RuntimeCompatibilityChecker().InspectAsync(package, game, Hd2IASRoute.Native);
        Assert.Equal("9.9.9", report.InstalledBuild!.Version);
        Assert.Equal(Hd2IASRoute.Lua, report.InstalledBuild.Route);
        Assert.Null(report.InstalledVersion); Assert.Null(report.InstalledRoute);
        Assert.Equal(Hd2IASInstallationState.IncompleteOrUnknown, report.Installation);
        Assert.Contains(report.FilePlan, plan => plan.Path == "bin/addon64" && plan.Action == Hd2IASFileAction.BlockUnknown);
    }
}
