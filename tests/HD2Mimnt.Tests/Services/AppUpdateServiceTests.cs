using System.IO.Compression;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core;
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
    public async Task CheckAsync_FallsBackToSecondaryChannel_WhenPrimaryFails()
    {
        var service = CreateService(release: null,
            primary: new FakeReleaseClient(null, AppUpdateChannels.GitHub, new HttpRequestException("gh 不可用")),
            fallback: new FakeReleaseClient(CreateRelease("V99.0.0", CreateAsset("V99.0.0")),
                AppUpdateChannels.Codeberg));

        var result = await service.CheckAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal("99.0.0", result.Info!.Version);
    }

    [Fact]
    public async Task CheckAsync_UsesConfiguredChannelFirst()
    {
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync(SettingKeys.UpdatesChannel, AppUpdateChannels.Codeberg);
        var service = CreateService(release: null,
            primary: new FakeReleaseClient(CreateRelease("V1.0.0", CreateAsset("V1.0.0")),
                AppUpdateChannels.GitHub),
            fallback: new FakeReleaseClient(CreateRelease("V99.0.0", CreateAsset("V99.0.0")),
                AppUpdateChannels.Codeberg),
            settings: settings);

        var result = await service.CheckAsync();

        Assert.Equal("99.0.0", result.Info!.Version);
    }

    [Fact]
    public async Task CheckAsync_EmptyReleaseUrl_FallsBackToChannelReleasePage()
    {
        var release = new AppUpdateRelease("V99.0.0", "body", string.Empty, null, [CreateAsset("V99.0.0")]);
        var service = CreateService(release: null,
            fallback: new FakeReleaseClient(release, AppUpdateChannels.Codeberg));

        var result = await service.CheckAsync();

        Assert.Equal(AppUpdateConstants.CodebergReleasePageUrl, result.Info!.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_AllChannelsFail_ReportsEveryChannel()
    {
        var service = CreateService(release: null,
            primary: new FakeReleaseClient(null, AppUpdateChannels.GitHub, new HttpRequestException("gh")),
            fallback: new FakeReleaseClient(null, AppUpdateChannels.Codeberg, new HttpRequestException("cb")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync());

        Assert.Contains(AppUpdateChannels.GitHub, error.Message);
        Assert.Contains(AppUpdateChannels.Codeberg, error.Message);
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
    public async Task CheckAsync_ExposesAssetDigest()
    {
        var digest = "sha256:" + new string('6', 64);
        var release = CreateRelease("V99.0.0", CreateAsset("V99.0.0") with { Digest = digest });

        var result = await CreateService(release).CheckAsync();

        Assert.Equal(digest, result.Info!.AssetDigest);
    }

    [Fact]
    public async Task PrepareAsync_AssetDigest_PassedAsDownloadExpectedHash()
    {
        var digest = "sha256:" + new string('a', 64);
        var downloader = new FakeDownloadService((_, target) => File.WriteAllBytesAsync(target,
            CreateZipBytes(("HD2Mimnt.UI.exe", "payload"))));
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")), downloader: downloader);

        await service.PrepareAsync(CreateUpdate("99.0.0") with { AssetDigest = digest });

        Assert.Equal(digest, downloader.LastOptions!.ExpectedHash);
    }

    [Fact]
    public async Task PrepareAsync_UnrecognizedDigestShape_SkipsVerification()
    {
        var downloader = new FakeDownloadService((_, target) => File.WriteAllBytesAsync(target,
            CreateZipBytes(("HD2Mimnt.UI.exe", "payload"))));
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")), downloader: downloader);

        await service.PrepareAsync(CreateUpdate("99.0.0") with { AssetDigest = "sha1:deadbeef" });

        Assert.Null(downloader.LastOptions!.ExpectedHash);
    }

    [Fact]
    public async Task PrepareAsync_NetworkFailure_KeepsStagingForResume()
    {
        var downloader = new FakeDownloadService((_, _) => throw new IOException("网络中断"));
        var service = CreateService(CreateRelease("V99.0.0", CreateAsset("V99.0.0")), downloader: downloader);

        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(CreateUpdate("99.0.0")));
        // 网络失败保留版本暂存目录：HttpDownloadService 已把断点与 .part 落盘，下次续传
        Assert.True(Directory.Exists(Path.Combine(_stagingRoot, "99.0.0")));
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
        int? pid = null, string? installDirectory = null, FakeDownloadService? downloader = null,
        FakeReleaseClient? primary = null, FakeReleaseClient? fallback = null, SettingsService? settings = null)
    {
        downloadBytes ??= CreateZipBytes(("HD2Mimnt.UI.exe", "payload"), ("Assets/extra.dll", "extra"));
        downloader ??= new FakeDownloadService((_, target) => File.WriteAllBytesAsync(target, downloadBytes));
        return new AppUpdateService(
            primary ?? new FakeReleaseClient(release),
            downloader,
            stagingRootOverride: _stagingRoot,
            currentProcessId: pid,
            installDirectoryOverride: installDirectory ?? _installDirectory,
            fallbackReleaseClient: fallback,
            settings: settings);
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

    private sealed class FakeReleaseClient(AppUpdateRelease? release, string channel = AppUpdateChannels.GitHub,
        Exception? failure = null) : IAppUpdateReleaseClient
    {
        public string Channel => channel;

        public Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
            => failure is null ? Task.FromResult(release) : Task.FromException<AppUpdateRelease?>(failure);
    }

    private sealed class FakeDownloadService(Func<Uri, string, Task> onDownload) : IHttpDownloadService
    {
        public DownloadOptions? LastOptions { get; private set; }

        public Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            => throw new NotSupportedException();

        public Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
        {
            LastOptions = options;
            return onDownload(uri, targetPath);
        }
    }
}
