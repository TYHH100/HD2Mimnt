using System.Net;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>官方 Raw 优先；连接失败才顺序尝试用户指定的公益加速源，不调用 GitHub API。</summary>
public sealed class GitHubUpdateSource(HttpClient http, IHttpDownloadService downloader, TimeProvider? time = null) : IHd2IASUpdateSource
{
    private const string RepositoryPath = "Estecsky/HD2-Integrated-Armor-System/refs/heads/main/";
    private static readonly string[] Roots =
    [
        "https://raw.githubusercontent.com/",
        "https://api.gitproxy.dev/raw.githubusercontent.com/",
        "https://gh-proxy.com/raw.githubusercontent.com/",
        "https://fastgit.cc/https://raw.githubusercontent.com/"
    ];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _unavailableUntil = [];
    private readonly Dictionary<string, DateTimeOffset> _lastProxyRequest = [];
    private IReadOnlyList<Hd2IASUpdateVersion>? _versions;
    private DateTimeOffset _versionsExpire;
    private DateTimeOffset _retryAfter;

    public async Task<IReadOnlyList<Hd2IASUpdateVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_versions != null && _time.GetUtcNow() < _versionsExpire) return _versions;
            var history = await RequestAsync("UPDATE_HISTORY.txt", async uri =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("HD2Mimnt-ModManager");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var until = response.Headers.RetryAfter?.Date ?? _time.GetUtcNow() +
                        (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                    if (until > _retryAfter) _retryAfter = until;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // 只认独占一行的版本号；说明正文中的旧版本号不能产生下载候选。
            _versions = Array.AsReadOnly(history.TrimStart('\uFEFF').Split('\n').Select(line => line.Trim())
                .Where(IsVersion).Distinct(StringComparer.Ordinal)
                .OrderByDescending(Version.Parse).Select(version => new Hd2IASUpdateVersion(version)).ToArray());
            _versionsExpire = _time.GetUtcNow().AddMinutes(15);
            return _versions;
        }
        finally { _gate.Release(); }
    }

    public async Task<Stream> DownloadAsync(Hd2IASUpdateVersion version, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsVersion(version.Version)) throw new InvalidDataException("Invalid HD2IAS version.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = $"updates/{version.Version}/HD2IAS-{version.Version}-update.zip";
            return await RequestAsync(path, async uri =>
            {
                try { return await Download(uri); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 只尝试同源历史文件名，不把文件不存在当成网络故障。
                    var legacy = new Uri(uri.AbsoluteUri[..^"update.zip".Length] + "direct-update.zip");
                    await WaitForProxyAsync(legacy, cancellationToken);
                    return await Download(legacy);
                }
            }, cancellationToken);
        }
        finally { _gate.Release(); }

        Task<Stream> Download(Uri uri) => downloader.DownloadAsync(uri,
            new DownloadOptions { EnableResume = false, MaxParallelSegments = 1, MaxRetries = 0 }, progress, cancellationToken);
    }

    private async Task<T> RequestAsync<T>(string path, Func<Uri, Task<T>> request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_time.GetUtcNow() < _retryAfter)
            throw new HttpRequestException("HD2IAS 更新请求冷却中，请稍后重试。", null, HttpStatusCode.TooManyRequests);
        Exception? last = null;
        foreach (var root in Roots)
        {
            token.ThrowIfCancellationRequested();
            var uri = new Uri(root + RepositoryPath + path);
            if (_unavailableUntil.TryGetValue(uri.Host, out var until) && _time.GetUtcNow() < until) continue;
            try
            {
                await WaitForProxyAsync(uri, token);
                return await request(uri);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = _time.GetUtcNow().AddMinutes(5);
                if (retry > _retryAfter) _retryAfter = retry;
                throw; // 不通过轮换公益源规避服务端限流。
            }
            catch (Exception error) when (!token.IsCancellationRequested && IsConnectionFailure(error))
            {
                _unavailableUntil[uri.Host] = _time.GetUtcNow().AddMinutes(5);
                last = error;
            }
        }
        token.ThrowIfCancellationRequested();
        _retryAfter = _time.GetUtcNow().AddMinutes(1);
        throw new HttpRequestException("GitHub 直连及备用加速源暂不可用，请稍后重试或选择本地运行包。", last);
    }

    private async Task WaitForProxyAsync(Uri uri, CancellationToken token)
    {
        if (uri.Host == "raw.githubusercontent.com") return;
        var now = _time.GetUtcNow();
        if (_lastProxyRequest.TryGetValue(uri.Host, out var last) && last.AddSeconds(2) > now)
            await Task.Delay(last.AddSeconds(2) - now, _time, token);
        token.ThrowIfCancellationRequested();
        _lastProxyRequest[uri.Host] = _time.GetUtcNow();
    }

    private static bool IsConnectionFailure(Exception error) => error is OperationCanceledException or
        HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout };

    private static bool IsVersion(string value) => value.Length > 0 &&
        value.All(character => char.IsAsciiDigit(character) || character == '.') && Version.TryParse(value, out _);
}
