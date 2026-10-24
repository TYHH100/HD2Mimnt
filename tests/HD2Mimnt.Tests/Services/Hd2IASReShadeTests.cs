using System.Text;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Theory]
    [InlineData("OVERLAY", false)]
    [InlineData("STYLE", true)]
    public async Task Uninstall_ClearsReShadeSystemFontReferencesAndPreservesOtherConfig(string section, bool relative)
    {
        var zip = CreatePackage(includeBuild: true, includeFont: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync(); await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var font = relative ? "./HD2IndependentRig/fonts/NotoSansCJKsc-Regular.otf"
            : Path.Combine(game, "bin", "HD2IndependentRig") + "/fonts/NotoSansCJKsc-Regular.otf";
        var before = $"\uFEFF; 中文配置保留\r\n[HD2Interface]\r\nprevious_font=\r\n[{section}]\r\nFont={font}\r\nEditorFont={font}\r\nLatinFont={font}\r\nFontSize=13.000000\r\n[OTHER]\r\nFont={font}\r\n";
        var config = Path.Combine(game, "bin", "ReShade.ini"); await File.WriteAllBytesAsync(config, Encoding.UTF8.GetBytes(before));
        var result = await service.UninstallAsync(game);
        var expected = before.Replace($"Font={font}\r\nEditorFont={font}\r\nLatinFont={font}", "Font=\r\nEditorFont=\r\nLatinFont=");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(config));
        Assert.Contains("bin/HD2IndependentRig/fonts/NotoSansCJKsc-Regular.otf", result.RemovedFiles);
        Assert.DoesNotContain("bin/ReShade.ini", result.RemovedFiles);
        Assert.False(File.Exists(Path.Combine(game, "bin", "HD2IndependentRig", "fonts", "NotoSansCJKsc-Regular.otf")));
        Assert.Equal("dxgi", await File.ReadAllTextAsync(Path.Combine(game, "bin", "dxgi.dll")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("system")]
    public async Task Uninstall_RestoresOnlyExistingPreviousNonSystemFont(string previous)
    {
        var zip = CreatePackage(includeBuild: true, includeFont: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync(); await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var font = "HD2IndependentRig/fonts/NotoSansCJKsc-Regular.otf";
        var oldFont = previous == "system" ? font : "自定义.ttf";
        if (previous == "valid") await File.WriteAllTextAsync(Path.Combine(game, "bin", oldFont), "user font");
        var config = Path.Combine(game, "bin", "ReShade.ini");
        await File.WriteAllTextAsync(config, $"[HD2Interface]\nprevious_font={oldFont}\n[OVERLAY]\nFont={font}\n");
        await service.UninstallAsync(game);
        var expected = $"[HD2Interface]\nprevious_font={(previous == "system" ? "" : oldFont)}\n[OVERLAY]\nFont={(previous == "valid" ? oldFont : "")}\n";
        Assert.Equal(expected, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Uninstall_DoesNotRewriteUserSelectedReShadeFont()
    {
        var zip = CreatePackage(includeBuild: true, includeFont: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync(); await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var before = Encoding.UTF8.GetBytes("[OVERLAY]\nFont=HD2IndependentRig/fonts-custom/user.otf\nFontSize=17\n[HD2Interface]\nprevious_font=old.ttf\n");
        var config = Path.Combine(game, "bin", "ReShade.ini"); await File.WriteAllBytesAsync(config, before);
        var timestamp = File.GetLastWriteTimeUtc(config);
        await service.UninstallAsync(game);
        Assert.Equal(before, await File.ReadAllBytesAsync(config)); Assert.Equal(timestamp, File.GetLastWriteTimeUtc(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_FailureOrCancellationAfterFontCleanup_RestoresConfigAndRuntime(bool cancel)
    {
        var zip = CreatePackage(includeBuild: true, includeFont: true); var game = CreateGame("native");
        var (service, store, storage) = await InstallerAsync(); await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var before = Encoding.UTF8.GetBytes("[OVERLAY]\r\nFont=HD2IndependentRig/fonts/NotoSansCJKsc-Regular.otf\r\n");
        var config = Path.Combine(game, "bin", "ReShade.ini"); await File.WriteAllBytesAsync(config, before);
        var revision = (await store.ReadAsync(game))!.Revision;
        using var cancellation = new CancellationTokenSource(); var observed = false;
        var sawCleanConfig = false; var addonAlreadyRemoved = false;
        var progress = new IASInstallProgress(message =>
        {
            if (message != "卸载：bin/HD2IndependentRig.build.json") return;
            observed = true; sawCleanConfig = File.ReadAllText(config) == "[OVERLAY]\r\nFont=\r\n";
            addonAlreadyRemoved = !File.Exists(Path.Combine(game, "bin", "addon64"));
            if (cancel) cancellation.Cancel(); else throw new IOException("uninstall after ReShade cleanup");
        });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UninstallAsync(game, progress, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => service.UninstallAsync(game, progress, cancellation.Token));
        Assert.True(observed); Assert.True(sawCleanConfig); Assert.True(addonAlreadyRemoved);
        Assert.Equal(before, await File.ReadAllBytesAsync(config));
        Assert.Equal(revision, (await store.ReadAsync(game))!.Revision);
        Assert.Equal("native-addon", await File.ReadAllTextAsync(Path.Combine(game, "bin", "addon64")));
        Assert.True(File.Exists(Path.Combine(game, "bin", "HD2IndependentRig", "fonts", "NotoSansCJKsc-Regular.otf")));
        Assert.Empty(Directory.GetFiles(storage, "pending.json", SearchOption.AllDirectories));
    }
}
