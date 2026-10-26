using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideLoaderPackageTests
{
    private readonly DarktideLoaderPackageInspector _inspector = new();

    [Fact]
    public void Inspect_FullPackage_ClassifiesByContent()
    {
        using var env = new DarktideTestEnvironment();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader.zip", full: true);

        var info = _inspector.Inspect(path);

        Assert.Null(info.Error);
        Assert.Equal(DarktideReleasePackageType.Full, info.Type);
        Assert.True(info.HasLoadOrder);
        Assert.True(info.IsInstallable);
    }

    [Fact]
    public void Inspect_UpdateOnlyPackage_ClassifiesByContent()
    {
        using var env = new DarktideTestEnvironment();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader-UpdateOnly.zip", full: false);

        var info = _inspector.Inspect(path);

        Assert.Null(info.Error);
        Assert.Equal(DarktideReleasePackageType.UpdateOnly, info.Type);
        Assert.False(info.HasLoadOrder);
    }

    [Fact]
    public void Inspect_UpdateOnlyNamedPackageWithLoadOrder_Rejects()
    {
        using var env = new DarktideTestEnvironment();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader-UpdateOnly.zip", full: true);

        var info = _inspector.Inspect(path);

        Assert.NotNull(info.Error);
        Assert.Contains("仅更新包", info.Error);
    }

    [Fact]
    public void Inspect_UnexpectedModFile_Rejects()
    {
        using var env = new DarktideTestEnvironment();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader.zip", full: true,
            extra: new[] { ("mods/Evil/Evil.mod", "return {}") });

        var info = _inspector.Inspect(path);

        Assert.NotNull(info.Error);
        Assert.Contains("未识别", info.Error);
    }

    [Fact]
    public void Inspect_GameExecutablePath_Rejects()
    {
        using var env = new DarktideTestEnvironment();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader.zip", full: true,
            extra: new[] { ("binaries/Darktide.exe", "not a loader file") });

        var info = _inspector.Inspect(path);

        Assert.NotNull(info.Error);
        Assert.Contains("未识别", info.Error);
    }

    [Fact]
    public void Inspect_MissingPatcher_Rejects()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "no-patcher.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            DarktideTestEnvironment.AddEntry(archive, "binaries/mod_loader", "x");
            DarktideTestEnvironment.AddEntry(archive, "mods/base/mod_manager.lua", "x");
            DarktideTestEnvironment.AddEntry(archive, "toggle_darktide_mods.bat", "x");
        }

        var info = _inspector.Inspect(path);

        Assert.NotNull(info.Error);
        Assert.Contains("dtkit-patch", info.Error);
    }

    [Fact]
    public async Task InstallFull_PreservesExistingLoadOrderAndBacksUpFiles()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.WriteLoadOrder("-- user\r\nAlpha\r\n");
        var before = await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot));
        var path = env.CreateDmlPackage("Darktide-Mod-Loader.zip", full: true);
        var info = _inspector.Inspect(path);
        var installer = new DarktideLoaderInstaller();

        var result = await installer.InstallAsync(env.GameRoot, path, info, env.Storage.Temp, env.Storage.LoaderBackups);

        Assert.True(result.Success);
        Assert.True(result.PreservedLoadOrder);
        Assert.Equal(before, await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot)));
        Assert.Equal("return { loader = 2 }", await File.ReadAllTextAsync(DarktidePaths.DmlCore(env.GameRoot)));
        Assert.NotNull(result.BackupDirectory);
        Assert.True(Directory.Exists(result.BackupDirectory));
    }

    [Fact]
    public async Task InstallUpdateOnly_WithoutDml_Fails()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame(withDml: false);
        var path = env.CreateDmlPackage("Darktide-Mod-Loader-UpdateOnly.zip", full: false);
        var info = _inspector.Inspect(path);
        var installer = new DarktideLoaderInstaller();

        var result = await installer.InstallAsync(env.GameRoot, path, info, env.Storage.Temp, env.Storage.LoaderBackups);

        Assert.False(result.Success);
        Assert.Contains("不能使用仅更新包", result.Error);
    }

    [Fact]
    public async Task InstallUpdateOnly_WithDml_SucceedsAndKeepsLoadOrder()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.WriteLoadOrder("-- user\r\nAlpha\r\n");
        var before = await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot));
        var path = env.CreateDmlPackage("Darktide-Mod-Loader-UpdateOnly.zip", full: false);
        var info = _inspector.Inspect(path);
        var installer = new DarktideLoaderInstaller();

        var result = await installer.InstallAsync(env.GameRoot, path, info, env.Storage.Temp, env.Storage.LoaderBackups);

        Assert.True(result.Success);
        Assert.True(result.PreservedLoadOrder);
        Assert.Equal(before, await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot)));
    }

    [Fact]
    public async Task Install_LockedTarget_RollsBackEarlierFiles()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        var path = env.CreateDmlPackage("Darktide-Mod-Loader.zip", full: true);
        var info = _inspector.Inspect(path);
        var installer = new DarktideLoaderInstaller();
        var baseManager = Path.Combine(env.GameRoot, "mods", "base", "mod_manager.lua");
        await using var lockStream = new FileStream(baseManager, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await installer.InstallAsync(env.GameRoot, path, info, env.Storage.Temp, env.Storage.LoaderBackups);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("return {}", await File.ReadAllTextAsync(DarktidePaths.DmlCore(env.GameRoot)));
    }
}
