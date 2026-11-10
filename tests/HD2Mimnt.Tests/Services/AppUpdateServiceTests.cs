using System.IO.Compression;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Tests.Services;

public sealed class AppUpdateServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _stagingRoot;
    private readonly string _installDirectory;

    public AppUpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-app-update-tests", Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(_root, "staging");
        _installDirectory = Path.Combine(_root, "install");
        Directory.CreateDirectory(_installDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("V99.0.0", true)]
    [InlineData("V0.0.1", false)]
    [InlineData("V0.1.1-alpha", false)] // 同号预发布不高于当前正式版本
    public async Task CheckAsync_ComparesLatestTagWithAppVersion(string tag, bool expectedHasUpdate)
    {
        var release = CreateRelease(tag, CreateAsset(tag));
        var service = CreateService(release);

        var result = await service.CheckAsync();

        Assert.Equal(expectedHasUpdate, result.HasUpdate);
        Assert.Equal(tag.TrimStart('V'), result.Info!.Version);
    }

    [Fact]
    public async Task CheckAsync_MatchesInstallerAssetByName()
    {
        var asset = CreateAsset("V99.0.0");
        var release = CreateRelease("V99.0.0", asset, CreateAsset("V99.0.0", "HD2Mimnt-99.0.0-win-x64-symbols.zip"));
        var service = CreateService(release);

        var result = await service.CheckAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal(asset.DownloadUrl, result.Info!.AssetUrl);
        Assert.Equal(asset.Name, result.Info!.AssetName);
    }

    [Fact]
    public async Task CheckAsync_NoMatchingAsset_FallsBackToManualDownload()
    {
        var release = CreateRelease("V99.0.0", CreateAsset("V99.0.0", "unexpected-name.zip"));
        var service = CreateService(release);

        var result = await service.CheckAsync();

        Assert.True(result.HasUpdate);
        Assert.Null(result.Info!.AssetUrl);
        Assert.Null(result.Info!.AssetName);
    }

    [Fact]
    public async Task CheckAsync_MissingRelease_Throws()
    {
        var service = CreateService(release: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task PrepareAsync_DownloadsExtractsAndWritesInstallerScript()
    {
        var pid = 4711;
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")), pid: pid);
        // 残留的旧版本暂存目录应被清理。
        var stale = Path.Combine(_stagingRoot, "98.0.0");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "old.zip"), "stale");

        var staging = await service.PrepareAsync(CreateUpdate("99.0.0"));

        var stagedExe = Path.Combine(_stagingRoot, "99.0.0", "staged", "HD2Mimnt.UI.exe");
        Assert.True(File.Exists(stagedExe));
        Assert.Equal("payload", await File.ReadAllTextAsync(stagedExe));
        Assert.Equal(_installDirectory, staging.TargetDirectory);
        Assert.True(File.Exists(staging.InstallerScriptPath));
        Assert.False(Directory.Exists(stale)); // 旧暂存目录已清理

        var script = await File.ReadAllTextAsync(staging.InstallerScriptPath);
        Assert.Contains("4711", script);
        Assert.Contains(ScriptSingleQuoted(_installDirectory), script);
        Assert.Contains(ScriptSingleQuoted(staging.StagingDirectory), script);
        Assert.Contains("'HD2Mimnt.UI.exe'", script);
    }

    [Fact]
    public async Task PrepareAsync_MissingAsset_ThrowsAndLeavesNoStaging()
    {
        var service = CreateService(CreateRelease("V99.0.0"));
        var update = CreateUpdate("99.0.0") with { AssetUrl = null, AssetName = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(update));
        Assert.False(Directory.Exists(_stagingRoot));
    }

    [Fact]
    public async Task PrepareAsync_ArchiveWithoutMainExecutable_ThrowsAndCleansUp()
    {
        var bytes = CreateZipBytes(("Readme.txt", "nothing to install"));
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")),
            downloadBytes: bytes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(CreateUpdate("99.0.0")));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "99.0.0"))); // 失败后不残留半成品
        _ = error;
    }

    [Fact]
    public async Task PrepareAsync_InstallDirectoryMissing_ThrowsBeforeDownload()
    {
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")),
            installDirectory: Path.Combine(_root, "missing-install"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(CreateUpdate("99.0.0")));
        Assert.False(Directory.Exists(_stagingRoot)); // 未开始下载
    }

    [Fact]
    public void LaunchPendingInstaller_MissingScript_Throws()
    {
        var service = CreateService(CreateRelease("V99.0.0"));
        var staging = new AppUpdateStaging("99.0.0", _installDirectory,
            Path.Combine(_stagingRoot, "99.0.0", "staged"), Path.Combine(_stagingRoot, "99.0.0", "apply-update.ps1"));

        Assert.Throws<FileNotFoundException>(() => service.LaunchPendingInstaller(staging));
    }

    private AppUpdateService CreateService(AppUpdateRelease? release, byte[]? downloadBytes = null,
        int? pid = null, string? installDirectory = null)
    {
        downloadBytes ??= CreateZipBytes(("HD2Mimnt.UI.exe", "payload"), ("Assets/extra.dll", "extra"));
        return new AppUpdateService(
            new FakeReleaseClient(release),
            new FakeDownloadService((_, target) => File.WriteAllBytesAsync(target, downloadBytes)),
            stagingRootOverride: _stagingRoot,
            currentProcessId: pid,
            installDirectoryOverride: installDirectory ?? _installDirectory);
    }

    private static AppUpdateInfo CreateUpdate(string version) => new(
        version, "changelog", "https://example.test/releases/v" + version,
        $"https://example.test/download/HD2Mimnt-{version}-win-x64.zip",
        $"HD2Mimnt-{version}-win-x64.zip", null);

    private static AppUpdateRelease CreateRelease(string tag, params AppUpdateReleaseAsset[] assets) => new(
        tag, "release body", $"https://example.test/releases/tag/{tag}", null, assets);

    private static AppUpdateReleaseAsset CreateAsset(string tag, string? name = null)
    {
        var version = tag.TrimStart('V');
        return new AppUpdateReleaseAsset(name ?? $"HD2Mimnt-{version}-win-x64.zip",
            $"https://example.test/download/{name ?? $"HD2Mimnt-{version}-win-x64.zip"}");
    }

    private static byte[] CreateZipBytes(params (string Name, string Content)[] entries)
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return archiveStream.ToArray();
    }

    private static string ScriptSingleQuoted(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed class FakeReleaseClient(AppUpdateRelease? release) : IAppUpdateReleaseClient
    {
        public Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(release);
    }

    private sealed class FakeDownloadService(Func<Uri, string, Task> onDownload) : IHttpDownloadService
    {
        public Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            => throw new NotSupportedException();

        public Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            => onDownload(uri, targetPath);
    }
}
