using System.Net;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    private static readonly string[] IASRawRoots =
    [
        "https://raw.githubusercontent.com/",
        "https://api.gitproxy.dev/raw.githubusercontent.com/",
        "https://gh-proxy.com/raw.githubusercontent.com/",
        "https://fastgit.cc/https://raw.githubusercontent.com/"
    ];
    private const string IASRepository = "Estecsky/HD2-Integrated-Armor-System/refs/heads/main/";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GitHubVersions_FallsBackInOrderAndSharesFifteenMinuteCache(int workingSource)
    {
        var requests = new List<string>(); var time = new IASClock();
        using var handler = new IASHttpHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            var success = request.RequestUri.AbsoluteUri == IASRawRoots[workingSource] + IASRepository + "UPDATE_HISTORY.txt";
            return Task.FromResult(new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("1.4.5\n1.4.2\n") });
        }, anchorContent: _ => "1.4.5\n1.4.2\n"u8.ToArray());
        using var http = new HttpClient(handler);
        var source = new GitHubUpdateSource(http, new HttpDownloadService(http), time);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetVersionsAsync()));
        Assert.All(results, versions => Assert.Equal(new[] { "1.4.5", "1.4.2" }, versions.Select(v => v.Version)));
        Assert.Equal(IASRawRoots.Take(workingSource + 1).Select(root => root + IASRepository + "UPDATE_HISTORY.txt"), requests);
        time.Advance(TimeSpan.FromMinutes(14)); await source.GetVersionsAsync();
        Assert.Equal(workingSource + 1, requests.Count);
        time.Advance(TimeSpan.FromMinutes(2)); await source.GetVersionsAsync();
        Assert.Equal(2 * (workingSource + 1), requests.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GitHubDownload_FallbackUsesOneConnectionNoRangeRetriesAndExactProvidedUrls(int workingSource)
    {
        var bytes = await File.ReadAllBytesAsync(CreatePackage());
        var requests = new List<string>();
        using var http = new HttpClient(new IASHttpHandler((_, _) => throw new Exception("Unexpected content request"),
            anchorContent: _ => bytes));
        var downloader = new IASDownloadSpy((uri, options, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests.Add(uri.AbsoluteUri);
            Assert.Equal(1, options!.MaxParallelSegments); Assert.Equal(0, options.MaxRetries); Assert.False(options.EnableResume);
            if (uri.AbsoluteUri != IASRawRoots[workingSource] + IASRepository + "updates/1.4.4/HD2IAS-1.4.4-update.zip")
                throw new HttpRequestException("connection unavailable");
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        });
        await using var stream = await new GitHubUpdateSource(http, downloader).DownloadAsync(new("1.4.4"));
        Assert.Equal(bytes.Length, stream.Length);
        Assert.Equal(IASRawRoots.Take(workingSource + 1).Select(root => root + IASRepository + "updates/1.4.4/HD2IAS-1.4.4-update.zip"), requests);
    }

    [Fact]
    public async Task GitHubVersions_AllSourcesFail_CoolsDownWithoutRepeatingRequests()
    {
        using var handler = new IASHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var http = new HttpClient(handler); var time = new IASClock();
        var source = new GitHubUpdateSource(http, new HttpDownloadService(http), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(2, handler.Calls); // 锚点探测失败 + 仅官方直连
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(2, handler.Calls); // 冷却期内不请求
        time.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(3, handler.Calls); // 官方源不可用窗口内只补了锚点探测。
        time.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task GitHubVersions_Proxy429_HonorsRetryAfterAndDoesNotRotateToMoreProxies()
    {
        using var handler = new IASHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            Assert.Equal("api.gitproxy.dev", request.RequestUri.Host);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }, anchorContent: _ => [1]);
        using var http = new HttpClient(handler); var time = new IASClock();
        var source = new GitHubUpdateSource(http, new HttpDownloadService(http), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(3, handler.Calls); // 锚点探测 + 官方失败 + 公益源 429
        time.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(3, handler.Calls); // RetryAfter 冷却期内不请求
        time.Advance(TimeSpan.FromMinutes(55));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetVersionsAsync());
        Assert.Equal(6, handler.Calls);
    }

    [Fact]
    public async Task GitHubProxy_RequestSpacingCanBeCancelledAndFailedDirectIsNotProbedAgain()
    {
        using var handler = new IASHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Host == "raw.githubusercontent.com" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("1.4.4") }),
            anchorContent: path => path.Contains("UPDATE_HISTORY.txt") ? "1.4.4"u8.ToArray() : [1]);
        using var http = new HttpClient(handler); var time = new IASClock(); var downloads = 0;
        var source = new GitHubUpdateSource(http, new IASDownloadSpy((uri, _, _) =>
        {
            Assert.Equal("api.gitproxy.dev", uri.Host); downloads++;
            return Task.FromResult<Stream>(new MemoryStream([1]));
        }), time);
        await source.GetVersionsAsync(); Assert.Equal(3, handler.Calls); // 锚点探测 + 官方失败 + 公益源
        using var cancellation = new CancellationTokenSource();
        var pending = source.DownloadAsync(new("1.4.4"), cancellationToken: cancellation.Token);
        Assert.False(pending.IsCompleted); Assert.Equal(0, downloads);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, downloads);
        time.Advance(TimeSpan.FromSeconds(3));
        await using var stream = await source.DownloadAsync(new("1.4.4"));
        Assert.Equal(1, downloads); Assert.Equal(4, handler.Calls); // 版本轮 3 次 + zip 锚点探测 1 次
    }

    [Fact]
    public async Task GitHubDownload_ValidCacheCoalescesRequestsAndCorruptCacheIsRedownloaded()
    {
        var bytes = await File.ReadAllBytesAsync(CreatePackage());
        using var handler = PackageHandler(bytes, "1.4.4", "HD2IAS-1.4.4-update.zip");
        using var http = new HttpClient(handler); var service = UpdateService(http);
        var paths = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.DownloadUpdateAsync(new("1.4.4"))));
        Assert.All(paths, path => Assert.Equal(paths[0], path)); Assert.Equal(2, handler.Calls); // 锚点探测 + 下载
        await File.WriteAllTextAsync(paths[0], "corrupt-cache");
        Assert.Equal(paths[0], await service.DownloadUpdateAsync(new("1.4.4")));
        Assert.Equal(3, handler.Calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0])); // 锚点已缓存
        AssertNoDownloadRemnants(paths[0]);
    }

    [Fact]
    public async Task GitHubDownload_InvalidProxyPayloadDoesNotTryOtherSources()
    {
        var hosts = new List<string>();
        using var handler = new IASHttpHandler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(request.RequestUri.Host == "raw.githubusercontent.com"
                ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent("not-a-zip") });
        }, anchorContent: _ => System.Text.Encoding.UTF8.GetBytes("not-a-zip"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService(http).DownloadUpdateAsync(new("1.4.4")));
        Assert.Equal(new[] { "raw.githubusercontent.com", "api.gitproxy.dev" }, hosts);
        AssertNoDownloadRemnants(null);
    }

    [Fact]
    public async Task GitHubDownload_TamperedProxyContent_IsRejectedByOfficialAnchor()
    {
        // 官方锚点指向真实包内容，公益源投毒返回不同字节：必须整体拒绝且不落缓存
        var real = await File.ReadAllBytesAsync(CreatePackage());
        using var handler = new IASHttpHandler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Host == "raw.githubusercontent.com" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            { Content = new ByteArrayContent("tampered-payload"u8.ToArray()) }),
            anchorContent: _ => real);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService(http).DownloadUpdateAsync(new("1.4.4")));
        AssertNoDownloadRemnants(null);
    }

    private sealed class IASClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class IASDownloadSpy(Func<Uri, DownloadOptions?, CancellationToken, Task<Stream>> download) : IHttpDownloadService
    {
        public Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default, string? resumeKey = null) => download(uri, options, cancellationToken);
        public Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            => throw new NotSupportedException();
    }
}
