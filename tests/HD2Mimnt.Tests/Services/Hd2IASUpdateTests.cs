using System.Net;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Fact]
    public async Task GitHubVersions_ReadsRawHistoryAndSortsStandaloneVersionNumbers()
    {
        using var handler = new IASHttpHandler((request, _) =>
        {
            Assert.Equal("https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/refs/heads/main/UPDATE_HISTORY.txt", request.RequestUri!.AbsoluteUri);
            Assert.Contains("HD2Mimnt", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "\uFEFF1.9.0\r\n- 从 9.9.9 更新后请重新部署。\r\n\r\n 1.10.0 \n- 更新说明\n1.9.0\n../outside\nnotes\n", Encoding.UTF8, "text/plain") });
        });
        using var client = new HttpClient(handler);
        var versions = await new GitHubUpdateSource(client, new HttpDownloadService(client)).GetVersionsAsync();
        Assert.Equal(new[] { "1.10.0", "1.9.0" }, versions.Select(version => version.Version));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHubDownload_UsesRawZipAndValidatesManifestsBeforePublishing(bool legacyName)
    {
        var bytes = await File.ReadAllBytesAsync(CreatePackage());
        var name = legacyName ? "HD2IAS-1.4.4-direct-update.zip" : "HD2IAS-1.4.4-update.zip";
        using var handler = PackageHandler(bytes, "1.4.4", name);
        using var client = new HttpClient(handler);
        var service = UpdateService(client);
        var path = await service.DownloadUpdateAsync(new("1.4.4"));
        Assert.Equal(Path.Combine(_root, "cache", "HD2IAS-1.4.4-update.zip"), path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True((await service.InspectAsync(path, null, Hd2IASRoute.Native)).Package.IsValid);
        Assert.Equal(legacyName ? 2 : 1, handler.Calls);
        AssertNoDownloadRemnants(path);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("version")]
    [InlineData("archive")]
    public async Task GitHubDownload_InvalidPayloadPreservesPreviousCacheAndCleansTemporaryFiles(string failure)
    {
        var bytes = failure == "archive" ? Encoding.UTF8.GetBytes("not a zip") :
            await File.ReadAllBytesAsync(CreatePackage(corrupt: failure == "manifest"));
        var version = failure == "version" ? "1.4.5" : "1.4.4";
        using var handler = PackageHandler(bytes, version, $"HD2IAS-{version}-update.zip");
        using var client = new HttpClient(handler);
        var path = Path.Combine(_root, "cache", $"HD2IAS-{version}-update.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "previous-cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService(client).DownloadUpdateAsync(new(version)));
        Assert.Equal("previous-cache", await File.ReadAllTextAsync(path));
        AssertNoDownloadRemnants(path);
    }

    [Fact]
    public async Task GitHubDownload_TruncatedResponsePreservesPreviousCacheAndCleansTemporaryFiles()
    {
        var bytes = await File.ReadAllBytesAsync(CreatePackage());
        using var handler = PackageHandler(bytes, "1.4.4", "HD2IAS-1.4.4-update.zip", truncated: true);
        using var client = new HttpClient(handler);
        var path = Path.Combine(_root, "cache", "HD2IAS-1.4.4-update.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "previous-cache");
        await Assert.ThrowsAsync<IOException>(() => UpdateService(client).DownloadUpdateAsync(new("1.4.4")));
        Assert.Equal("previous-cache", await File.ReadAllTextAsync(path));
        AssertNoDownloadRemnants(path);
    }

    [Fact]
    public async Task GitHubDownload_CancelledTransferCleansAllPendingFiles()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new IASHttpHandler(async (request, token) =>
        {
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var task = UpdateService(client).DownloadUpdateAsync(new("1.4.4"), cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        AssertNoDownloadRemnants(null);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GitHubVersions_HttpFailureDoesNotCallApiOrRetry(HttpStatusCode status)
    {
        using var handler = new IASHttpHandler((request, _) =>
        {
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(status));
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubUpdateSource(client, new HttpDownloadService(client)).GetVersionsAsync());
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("- 从 1.4.4 更新至 1.4.5\n- 更新说明")]
    public async Task GitHubVersions_WithoutStandaloneVersion_ReturnsEmpty(string history)
    {
        using var handler = new IASHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(history) }));
        using var client = new HttpClient(handler);
        Assert.Empty(await new GitHubUpdateSource(client, new HttpDownloadService(client)).GetVersionsAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GitHubDownload_Non404ErrorDoesNotTryAnotherNameOrApi(HttpStatusCode status)
    {
        using var handler = new IASHttpHandler((request, _) =>
        {
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(status));
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => UpdateService(client).DownloadUpdateAsync(new("1.4.4")));
        Assert.Equal(status, error.StatusCode); Assert.Equal(1, handler.Calls);
        AssertNoDownloadRemnants(null);
    }

    private Hd2IASIntegrationService UpdateService(HttpClient client) => new(new PackageManifestReader(),
        new RuntimeCompatibilityChecker(), updates: new GitHubUpdateSource(client,
            new HttpDownloadService(client, resumeDirectory: Path.Combine(_root, "resume"), tempDirectory: Path.Combine(_root, "http-temp"))),
        updateCachePath: Path.Combine(_root, "cache"));

    private void AssertNoDownloadRemnants(string? cacheFile)
    {
        var cache = Path.Combine(_root, "cache");
        Assert.Equal(cacheFile == null ? [] : new[] { cacheFile }, Directory.Exists(cache) ? Directory.GetFiles(cache) : []);
        foreach (var folder in new[] { "resume", "http-temp" })
            Assert.Empty(Directory.Exists(Path.Combine(_root, folder)) ? Directory.GetFiles(Path.Combine(_root, folder), "*", SearchOption.AllDirectories) : []);
    }

    private static IASHttpHandler PackageHandler(byte[] bytes, string version, string name, bool truncated = false)
    {
        return new IASHttpHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            var root = $"https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/refs/heads/main/updates/{version}/";
            Assert.StartsWith(root, request.RequestUri!.AbsoluteUri);
            if (name.EndsWith("direct-update.zip") && request.RequestUri.AbsoluteUri == root + $"HD2IAS-{version}-update.zip")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Assert.Equal(root + name, request.RequestUri.AbsoluteUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (truncated) response.Content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(response);
        });
    }

    private sealed class IASHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return respond(request, cancellationToken); }
    }
}
