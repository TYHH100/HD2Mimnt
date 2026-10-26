using System.IO.Compression;
using System.Text;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideArchiveTests
{
    private readonly DarktideArchiveInspector _inspector = new();
    private readonly DarktideModInstaller _installer = new();

    [Fact]
    public void Inspect_NestedModFolder_FindsCandidate()
    {
        using var env = new DarktideTestEnvironment();
        var zip = env.CreateModPackage("mod.zip", "MyMod");

        var result = _inspector.Inspect(zip);

        Assert.Null(result.Error);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("MyMod", candidate.InternalName);
        Assert.Equal("MyMod", candidate.RelativeRoot);
        Assert.True(candidate.HasScripts);
        Assert.True(candidate.HasInfoJson);
    }

    [Fact]
    public void Inspect_WrapperFolder_FindsNestedCandidate()
    {
        using var env = new DarktideTestEnvironment();
        var zip = env.CreateModPackage("mod.zip", "MyMod", wrapper: "Package");

        var result = _inspector.Inspect(zip);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Package/MyMod", candidate.RelativeRoot);
        Assert.Equal("MyMod", candidate.InternalName);
    }

    [Fact]
    public void Inspect_MultipleMods_ReturnsAllCandidates()
    {
        using var env = new DarktideTestEnvironment();
        var zip = Path.Combine(env.Root, "multi.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            DarktideTestEnvironment.AddEntry(archive, "ModA/ModA.mod", "return {}");
            DarktideTestEnvironment.AddEntry(archive, "ModA/scripts/mods/ModA/ModA.lua", "return {}");
            DarktideTestEnvironment.AddEntry(archive, "ModB/ModB.mod", "return {}");
            DarktideTestEnvironment.AddEntry(archive, "ModB/scripts/mods/ModB/ModB.lua", "return {}");
        }

        var result = _inspector.Inspect(zip);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Inspect_PathTraversal_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();
        var zip = Path.Combine(env.Root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            DarktideTestEnvironment.AddEntry(archive, "../evil.txt", "x");

        var result = _inspector.Inspect(zip);

        Assert.NotNull(result.Error);
        Assert.Contains("不安全", result.Error);
    }

    [Fact]
    public void Inspect_SymlinkEntry_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();
        var zip = Path.Combine(env.Root, "link.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Mod/Mod.mod");
            entry.ExternalAttributes = 0xA000 << 16;
        }

        var result = _inspector.Inspect(zip);

        Assert.NotNull(result.Error);
        Assert.Contains("符号链接", result.Error);
    }

    [Fact]
    public void Inspect_WindowsReservedName_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();
        var zip = Path.Combine(env.Root, "reserved.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            DarktideTestEnvironment.AddEntry(archive, "Mod/Mod.mod", "return {}");
            DarktideTestEnvironment.AddEntry(archive, "Mod/NUL.txt", "x");
        }

        var result = _inspector.Inspect(zip);

        Assert.NotNull(result.Error);
        Assert.Contains("不安全", result.Error);
    }

    [Fact]
    public void Inspect_ClassifiesStandardBundleAndCustomInstallPackages()
    {
        using var env = new DarktideTestEnvironment();
        var single = env.CreateMultiModPackage("single.zip", new[] { ("AlphaMod", Array.Empty<string>()) });
        var bundle = env.CreateMultiModPackage("bundle.zip", new[]
        {
            ("CustomAssets", Array.Empty<string>()),
            ("PlayerModelHub", new[] { "CustomAssets" })
        });
        var custom = env.CreateMultiModPackage("custom.zip", new[]
        {
            ("CustomAssets", Array.Empty<string>()),
            ("ModelReplacementRegistry", Array.Empty<string>()),
            ("PlayerModelHub", new[] { "CustomAssets", "ModelReplacementRegistry" })
        }, withNativeExtras: true);

        Assert.Equal(DarktideArchivePackageKind.StandardMod, _inspector.Inspect(single).Kind);
        var bundleResult = _inspector.Inspect(bundle);
        Assert.Equal(DarktideArchivePackageKind.ModBundle, bundleResult.Kind);
        Assert.Equal(2, bundleResult.Candidates.Count);
        var customResult = _inspector.Inspect(custom);
        Assert.Equal(DarktideArchivePackageKind.CustomInstallRequired, customResult.Kind);
        Assert.Contains(customResult.ExtraFiles, path => path.StartsWith("binaries/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(customResult.ExtraFiles, path => path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Install_ReplacesExistingMod_AndRollsBackWhenPostCommitFails()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        var oldFolder = env.AddMod("MyMod");
        File.WriteAllText(Path.Combine(oldFolder, "old-marker.txt"), "old");
        var zip = env.CreateModPackage("new.zip", "MyMod", version: "2.0.0");
        var inspection = _inspector.Inspect(zip);
        var candidate = Assert.Single(inspection.Candidates);
        var staging = Path.Combine(env.GameRoot, "mods", DarktidePaths.StagingPrefix + Guid.NewGuid().ToString("N"));
        var stagedMod = Path.Combine(staging, "MyMod");
        await _inspector.ExtractCandidateAsync(zip, candidate, stagedMod);

        var threw = await Assert.ThrowsAsync<IOException>(() => _installer.InstallFromStagingAsync(
            env.GameRoot, stagedMod, "MyMod", allowReplace: true, register: true,
            afterCommit: _ => throw new InvalidOperationException("load order write failed")));

        Assert.Contains("原有文件已恢复", threw.Message);
        Assert.True(File.Exists(Path.Combine(env.GameRoot, "mods", "MyMod", "old-marker.txt")));
        Assert.Contains("1.0.0", await File.ReadAllTextAsync(Path.Combine(env.GameRoot, "mods", "MyMod", "info.json")));
        _installer.CleanupDirectory(staging);
    }

    [Fact]
    public async Task Install_NewMod_RequiresExplicitReplaceForExistingFolder()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("MyMod");
        var zip = env.CreateModPackage("new.zip", "MyMod", version: "2.0.0");
        var inspection = _inspector.Inspect(zip);
        var candidate = Assert.Single(inspection.Candidates);
        var staging = Path.Combine(env.GameRoot, "mods", DarktidePaths.StagingPrefix + Guid.NewGuid().ToString("N"));
        var stagedMod = Path.Combine(staging, "MyMod");
        await _inspector.ExtractCandidateAsync(zip, candidate, stagedMod);

        await Assert.ThrowsAsync<DarktideInstallConflictException>(() => _installer.InstallFromStagingAsync(
            env.GameRoot, stagedMod, "MyMod", allowReplace: false, register: true));
        Assert.True(Directory.Exists(Path.Combine(env.GameRoot, "mods", "MyMod")));
        _installer.CleanupDirectory(staging);
    }
}
