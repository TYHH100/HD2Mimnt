using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-ias-{Guid.NewGuid():N}");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task ReadPackage_ValidDualRoute_PreservesIndependentVersionsAndIdentities()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage());
        Assert.True(package.IsValid);
        Assert.Equal("1.4.4", package.Version);
        Assert.Equal("1.1.3", package.Foundation.Version);
        Assert.Equal(Hd2IASRoute.Lua, package.Lua.Route);
        Assert.Equal(Hd2IASRoute.Native, package.Native.Route);
        Assert.Equal("native-v1", package.Lua.AppearanceSource);
        Assert.NotEqual(package.Lua.IndependentLua.Identity, package.Native.IndependentLua.Identity);
        Assert.Equal(Hash("previous-addon"), Assert.Single(package.Lua.Files[0].PreviousSha256));
        Assert.Equal(9, package.FileChecks.Count);
        Assert.Equal("Update instructions", package.UpdateNotes);
    }

    [Theory]
    [InlineData("HD2RuntimeDistribution9", "lua", "bin/addon64", false)]
    [InlineData("HD2RuntimeDistribution1", "native", "bin/addon64", false)]
    [InlineData("HD2RuntimeDistribution1", "lua", "../dxgi.dll", false)]
    [InlineData("HD2RuntimeDistribution1", "lua", "C:/dxgi.dll", false)]
    [InlineData("HD2RuntimeDistribution1", "lua", "bin/addon64", true)]
    public async Task ReadPackage_UnsupportedOrInconsistentManifest_Rejects(string schema, string route, string target, bool duplicate)
    {
        var path = CreatePackage(schema: schema, luaRoute: route, target: target, duplicate: duplicate);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageManifestReader().ReadAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPackage_MissingOrTamperedPayload_ReportsInvalid(bool missing)
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage(corrupt: !missing, missing: missing));
        Assert.False(package.IsValid);
        Assert.Contains(package.FileChecks, check => check.Path == "RuntimePackage/addon64" &&
            check.State == (missing ? Hd2IASFileState.Missing : Hd2IASFileState.Unknown));
    }

    [Fact]
    public async Task Inspect_BothRoutes_IdentifiesCompleteRuntimeWithoutConfusingAppearanceSource()
    {
        var zip = CreatePackage();
        var game = CreateGame("lua");
        var service = new Hd2IASIntegrationService(new PackageManifestReader(), new RuntimeCompatibilityChecker());
        var lua = await service.InspectAsync(zip, game, Hd2IASRoute.Native);
        Assert.True(lua.IsGameCompatible);
        Assert.Equal(Hd2IASInstallationState.RuntimeMatches, lua.Installation);
        Assert.Equal(Hd2IASRoute.Lua, lua.InstalledRoute);
        Assert.Equal("1.4.4", lua.InstalledVersion);
        Assert.Contains(lua.RuntimeChecks, check => check.State == Hd2IASFileState.RecognizedPrevious);
        CreateGame("native");
        var native = await service.InspectAsync(zip, game, Hd2IASRoute.Lua);
        Assert.Equal(Hd2IASRoute.Native, native.InstalledRoute);
        Assert.All(native.FoundationChecks, check => Assert.Equal(Hd2IASFileState.Match, check.State));
    }

    [Fact]
    public async Task Inspect_UnknownDxgiAndRecognizedOlderAddon_PreservesFilesAndReportsUnknownVersion()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage());
        var game = CreateGame("lua");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "dxgi.dll"), "user-reshade");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "addon64"), "previous-addon");
        var report = await new RuntimeCompatibilityChecker().InspectAsync(package, game, Hd2IASRoute.Lua);
        Assert.Equal(Hd2IASInstallationState.IncompleteOrUnknown, report.Installation);
        Assert.Null(report.InstalledRoute); Assert.Null(report.InstalledVersion);
        Assert.Contains(report.RuntimeChecks, check => check.Path == "bin/dxgi.dll" && check.State == Hd2IASFileState.Unknown);
        Assert.Contains(report.RuntimeChecks, check => check.Path == "bin/addon64" && check.State == Hd2IASFileState.RecognizedPrevious);
        Assert.Equal("user-reshade", await File.ReadAllTextAsync(Path.Combine(game, "bin", "dxgi.dll")));
        Assert.Equal("previous-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
    }

    [Fact]
    public async Task Inspect_NoGameMissingFilesOrMismatchedGame_DoesNotClaimCompatibility()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage());
        var inspector = new RuntimeCompatibilityChecker();
        var noGame = await inspector.InspectAsync(package, null, Hd2IASRoute.Lua);
        Assert.Equal(Hd2IASInstallationState.NotChecked, noGame.Installation);
        Assert.False(noGame.IsGameCompatible);
        var missing = await inspector.InspectAsync(package, Path.Combine(_root, "empty"), Hd2IASRoute.Lua);
        Assert.Equal(Hd2IASInstallationState.Absent, missing.Installation);
        Assert.False(missing.IsGameCompatible);
        var game = CreateGame("lua");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "helldivers2.exe"), "changed-game");
        Assert.False((await inspector.InspectAsync(package, game, Hd2IASRoute.Lua)).IsGameCompatible);
    }

    [Fact]
    public async Task Inspect_InvalidPackageOrRetiredFiles_DoesNotClaimCompleteRuntime()
    {
        var package = await new PackageManifestReader().ReadAsync(CreatePackage(corrupt: true));
        var game = CreateGame("lua");
        var inspector = new RuntimeCompatibilityChecker();
        Assert.Null((await inspector.InspectAsync(package, game, Hd2IASRoute.Lua)).InstalledVersion);
        package = await new PackageManifestReader().ReadAsync(CreatePackage());
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "retired.dll"), "old-retired");
        var report = await inspector.InspectAsync(package, game, Hd2IASRoute.Lua);
        Assert.Null(report.InstalledVersion);
        Assert.Contains(report.RuntimeChecks, check => check.Path == "bin/retired.dll" && check.State == Hd2IASFileState.RecognizedPrevious);
    }

    [Fact]
    public async Task Inspect_PreCancelled_StopsWithoutChangingGame()
    {
        var zip = CreatePackage(); var game = CreateGame("lua");
        var before = await File.ReadAllBytesAsync(Path.Combine(game, "bin", "addon64"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Hd2IASIntegrationService(new PackageManifestReader(), new RuntimeCompatibilityChecker())
                .InspectAsync(zip, game, Hd2IASRoute.Lua, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(game, "bin", "addon64")));
    }

    [Theory]
    [InlineData(Hd2IASRoute.Lua)]
    [InlineData(Hd2IASRoute.Native)]
    public async Task Generate_BothRoutes_ValidatesSourceAndExportsOnlySystemData(Hd2IASRoute route)
    {
        var zip = CreatePackage(); var game = CreateGame("lua");
        var compiler = new TestCompiler();
        var service = new Hd2IASIntegrationService(new PackageManifestReader(), new RuntimeCompatibilityChecker(), compiler);
        var destination = Path.Combine(_root, "data.zip");
        var result = await service.GenerateAsync(zip, game, route, destination);
        Assert.Equal(route, result.Route); Assert.Equal(1, compiler.Calls);
        Assert.Equal(Path.Combine(game, "bin", "lua51.dll"), compiler.Path);
        using var output = ZipFile.OpenRead(destination);
        Assert.Equal(5, output.Entries.Count);
        Assert.DoesNotContain(output.Entries, entry => entry.Name.EndsWith(".dll"));
        using var patch = output.GetEntry("data/" + StingrayArchiveWriter.PatchName)!.Open();
        using var buffer = new MemoryStream(); await patch.CopyToAsync(buffer);
        var data = buffer.ToArray();
        Assert.Equal(0xf0000011U, BitConverter.ToUInt32(data));
        Assert.Equal(184UL, BitConverter.ToUInt64(data, 120));
        Assert.Equal(0xa14e8dfa2cd117e2UL, BitConverter.ToUInt64(data, 112));
        Assert.Equal(0x70217982548cf034UL, BitConverter.ToUInt64(data, 104));
        Assert.Equal(2U, BitConverter.ToUInt32(data, 188));
        Assert.Equal(compiler.Source, data[192..]);
        Assert.Equal(compiler.Source!.Length, BitConverter.ToInt32(data, 184));
        Assert.Equal(result.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(data[192..])));
        using var info = JsonDocument.Parse(output.GetEntry("mod_info.json")!.Open());
        Assert.Equal(route == Hd2IASRoute.Lua ? "lua" : "native", info.RootElement.GetProperty("pose_route").GetString());
        Assert.Equal("lua", info.RootElement.GetProperty("package_type").GetString());
        Assert.True(info.RootElement.GetProperty("requires_loader").GetBoolean());
        Assert.False(info.RootElement.GetProperty("loader_bundled").GetBoolean());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Generate_CompileFailureCancellationOrExistingOutput_DoesNotPublishOrOverwrite()
    {
        var zip = CreatePackage(); var game = CreateGame("lua");
        var compiler = new TestCompiler { Fail = true };
        var service = new Hd2IASIntegrationService(new PackageManifestReader(), new RuntimeCompatibilityChecker(), compiler);
        var destination = Path.Combine(_root, "failed.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateAsync(zip, game, Hd2IASRoute.Lua, destination));
        Assert.False(File.Exists(destination)); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        compiler.Fail = false; await File.WriteAllTextAsync(destination, "existing-output");
        await Assert.ThrowsAsync<IOException>(() => service.GenerateAsync(zip, game, Hd2IASRoute.Lua, destination));
        Assert.Equal("existing-output", await File.ReadAllTextAsync(destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(zip, game, Hd2IASRoute.Lua, Path.Combine(game, "data.zip")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var manifest = (await new PackageManifestReader().ReadAsync(zip)).Lua;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArsenalPackageWriter().WriteAsync(
            Path.Combine(_root, "cancelled.zip"), manifest, compiler.Source!, null, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_root, "cancelled.zip"))); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task SourceBuilder_NormalizesLineEndingsAndRejectsDifferentIdentityOrBody()
    {
        var zip = CreatePackage();
        var manifest = (await new PackageManifestReader().ReadAsync(zip)).Lua;
        var template = Encoding.UTF8.GetBytes(Template.Replace("\n", "\r\n"));
        var body = Encoding.UTF8.GetBytes("\uFEFFlocal route = \"lua\"\r\n\r\n");
        var source = IndependentLuaSourceBuilder.Build(body, template, manifest);
        Assert.Equal(manifest.IndependentLua.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(source)));
        Assert.Throws<InvalidDataException>(() => IndependentLuaSourceBuilder.Build(Encoding.UTF8.GetBytes("bad"), template, manifest));
        Assert.Throws<InvalidDataException>(() => IndependentLuaSourceBuilder.Build(body, template,
            manifest with { IndependentLua = manifest.IndependentLua with { Identity = Hash("other-identity") } }));
        var archive = StingrayArchiveWriter.Build(source, 0x70217982548cf034);
        archive[104] ^= 1;
        Assert.Throws<InvalidDataException>(() => StingrayArchiveWriter.ReadSource(archive, 0x70217982548cf034));
    }

    [Fact]
    public async Task Compiler_UnknownDllHash_RejectsBeforeNativeLoading()
    {
        Directory.CreateDirectory(_root); var dll = Path.Combine(_root, "fake-lua51.dll");
        await File.WriteAllTextAsync(dll, "not-a-dll");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LuaCompilerValidator().ValidateAsync(
            dll, Hash("other-dll"), Encoding.UTF8.GetBytes("return 1"), "test"));
    }

    private const string Template = "-- HD2-Addon: mods/hd2ir/runtime\nlocal identity = \"@@MODULE_IDENTITY@@\"\n@@RUNTIME_BODY@@\n";
    private sealed class TestCompiler : IHd2IASLuaCompiler
    {
        public int Calls; public bool Fail; public string? Path; public byte[]? Source;
        public Task ValidateAsync(string path, string expectedSha256, byte[] source, string module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Path = path; Source = source;
            if (Fail) throw new InvalidDataException("test compilation failed");
            return Task.CompletedTask;
        }
    }

    private string CreatePackage(string schema = "HD2RuntimeDistribution1", string luaRoute = "lua",
        string target = "bin/addon64", bool duplicate = false, bool corrupt = false, bool missing = false,
        string version = "1.4.4", string addonSuffix = "", bool includeBuild = false, string? previousBuildVersion = null)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"package-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var route in new[] { "lua", "native" })
        {
            var prefix = route == "lua" ? "RuntimePackage/" : "RuntimePackage/Native/";
            var body = $"local route = \"{route}\"";
            var addon = route + "-addon" + addonSuffix;
            var build = BuildContents(version, route);
            var source = Template.Replace("@@MODULE_IDENTITY@@", Hash(route + "-identity")).Replace("@@RUNTIME_BODY@@", body);
            AddJson(prefix + "package.json", new
            {
                schema, version, pose_route = route == "lua" ? luaRoute : route, appearance_source = "native-v1",
                game_exe_sha256 = Hash("game"), bundle_database_sha256 = Hash("database"), lua51_sha256 = Hash("lua51"),
                independent_lua = new { module = "mods/hd2ir/runtime", resource_id = "70217982548cf034",
                    body_sha256 = Hash(body), identity = Hash(route + "-identity"), source_sha256 = Hash(source) },
                files = new object[]
                {
                    new { path = "addon64", target, sha256 = Hash(addon), bytes = Encoding.UTF8.GetByteCount(addon), previous_sha256 = new[] { Hash("previous-addon") } },
                    new { path = "dxgi.dll", target = "bin/dxgi.dll", sha256 = Hash("dxgi"), bytes = 4 },
                    new { path = "runtime.lua", target = (string?)null, sha256 = Hash(body) },
                    new { path = "independent_loader.lua", target = (string?)null, sha256 = Hash(Template) }
                }.Concat(includeBuild ? new object[] { new { path = "HD2IndependentRig.build.json", target = (string?)null,
                    sha256 = Hash(build), previous_sha256 = previousBuildVersion == null ? Array.Empty<string>() : new[] { Hash(BuildContents(previousBuildVersion, route)) } } } : []).ToArray(),
                retired_runtime = new[] { new { target = "bin/retired.dll", previous_sha256 = new[] { Hash("old-retired") } } }
            });
            if (!missing || route != "lua") Add(prefix + "addon64", corrupt && route == "lua" ? "tampered" : addon);
            Add(prefix + "dxgi.dll", "dxgi"); Add(prefix + "runtime.lua", body); Add(prefix + "independent_loader.lua", Template);
            if (includeBuild) Add(prefix + "HD2IndependentRig.build.json", build);
        }
        AddJson("Foundation/foundation.json", new
        {
            schema = "HD2SystemFoundation2", version = "1.1.3", game_exe_sha256 = Hash("game"), bundle_database_sha256 = Hash("database"),
            files = new[] { new { path = "bin/HD2IndependentRig/runtime.ini", sha256 = Hash("ini") } }
        });
        Add("Foundation/bin/HD2IndependentRig/runtime.ini", "ini");
        AddJson("app/update_notes.json", new { version, notes = "Update instructions", manual_actions = new[] { "Regenerate on route change" } });
        if (duplicate) Add("runtimepackage/ADDON64", "duplicate");
        return path;

        void Add(string entry, string content)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        void AddJson(string entry, object content) => Add(entry, JsonSerializer.Serialize(content));
    }

    private static string BuildContents(string version, string route) => JsonSerializer.Serialize(new
        { schema = "HD2IndependentRigBuild1", version, pose_route = route });

    private string CreateGame(string route)
    {
        var game = Path.Combine(_root, "game");
        foreach (var (path, value) in new[] { ("bin/helldivers2.exe", "game"), ("data/bundle_database.data", "database"),
            ("bin/lua51.dll", "lua51"), ("bin/addon64", route + "-addon"), ("bin/dxgi.dll", "dxgi"), ("bin/HD2IndependentRig/runtime.ini", "ini") })
        {
            var fullPath = Path.Combine(game, path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, value);
        }
        return game;
    }
    public void Dispose() { foreach (var db in _iasDatabases) db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
