using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>
/// 官方 Raw 优先；连接失败才顺序尝试用户指定的公益加速源。
/// 每个文件先从 GitHub 官方 API 取内容锚点（git blob SHA-1 + 大小），
/// 下载后校验一致才放行——加速源无法替换内容；官方 API 不可用时仅允许官方直连。
/// </summary>
public sealed class GitHubUpdateSource(HttpClient http, IHttpDownloadService downloader, TimeProvider? time = null) : IHd2IASUpdateSource
{
    private const string RepositoryPath = "Estecsky/HD2-Integrated-Armor-System/refs/heads/main/";
    private const string OfficialHost = "raw.githubusercontent.com";
    private static readonly string[] Roots =
    [
        "https://raw.githubusercontent.com/",
        "https://api.gitproxy.dev/raw.githubusercontent.com/",
        "https://gh-proxy.com/raw.githubusercontent.com/",
        "https://fastgit.cc/https://raw.githubusercontent.com/"
    ];
    private static readonly string[] OfficialOnlyRoots = [Roots[0]];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _unavailableUntil = [];
    private readonly Dictionary<string, DateTimeOffset> _lastProxyRequest = [];
    private readonly Dictionary<string, (string Sha, long Size, DateTimeOffset Expires)> _anchors = [];
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
            EnsureNotCoolingDown();
            var anchor = await TryGetOfficialAnchorAsync("UPDATE_HISTORY.txt", cancellationToken);
            var bytes = await RequestAsync("UPDATE_HISTORY.txt", FetchBytesAsync,
                anchor is null ? OfficialOnlyRoots : Roots, cancellationToken);
            if (anchor is { } meta && !GitBlobShaMatches(bytes, meta))
            {
                throw new InvalidDataException("HD2IAS 更新历史与 GitHub 官方内容不符，已拒绝使用。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // 只认独占一行的版本号；说明正文中的旧版本号不能产生下载候选。
            var history = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            _versions = Array.AsReadOnly(history.Split('\n').Select(line => line.Trim())
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
            EnsureNotCoolingDown();
            var path = $"updates/{version.Version}/HD2IAS-{version.Version}-update.zip";
            var legacyPath = $"updates/{version.Version}/HD2IAS-{version.Version}-direct-update.zip";
            var anchor = await TryGetOfficialAnchorAsync(path, cancellationToken);

            var stream = await RequestAsync(path, async uri =>
            {
                try { return await Download(uri); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 只尝试同源历史文件名，不把文件不存在当成网络故障。
                    var legacy = new Uri(uri.AbsoluteUri[..^"update.zip".Length] + "direct-update.zip");
                    anchor = await TryGetOfficialAnchorAsync(legacyPath, cancellationToken);
                    if (anchor is null && legacy.Host != OfficialHost)
                    {
                        // 没有官方锚点时，公益加速源内容无法验证，拒绝经其下载。
                        throw new HttpRequestException("GitHub 官方 API 不可用，拒绝经加速源下载历史文件名。");
                    }
                    await WaitForProxyAsync(legacy, cancellationToken);
                    return await Download(legacy);
                }
            }, anchor is null ? OfficialOnlyRoots : Roots, cancellationToken);

            if (anchor is { } meta)
            {
                if (stream.Length != meta.Size || await ComputeGitBlobSha1Async(stream, cancellationToken) != meta.Sha)
                {
                    await stream.DisposeAsync();
                    throw new InvalidDataException($"HD2IAS 更新包 {version.Version} 与 GitHub 官方内容不符，已拒绝安装。");
                }
                stream.Position = 0;
            }

            return stream;
        }
        finally { _gate.Release(); }

        Task<Stream> Download(Uri uri) => downloader.DownloadAsync(uri,
            new DownloadOptions { EnableResume = false, MaxParallelSegments = 1, MaxRetries = 0 }, progress, cancellationToken);
    }

    private async Task<byte[]> FetchBytesAsync(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("HD2Mimnt-ModManager");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var until = response.Headers.RetryAfter?.Date ?? _time.GetUtcNow() +
                (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
            if (until > _retryAfter) _retryAfter = until;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private void EnsureNotCoolingDown()
    {
        if (_time.GetUtcNow() < _retryAfter)
            throw new HttpRequestException("HD2IAS 更新请求冷却中，请稍后重试。", null, HttpStatusCode.TooManyRequests);
    }

    /// <summary>从 GitHub 官方 Contents API 读取文件的 git blob SHA-1 与大小作为内容锚点（缓存 15 分钟）。</summary>
    private async Task<(string Sha, long Size)?> TryGetOfficialAnchorAsync(string path, CancellationToken token)
    {
        if (_anchors.TryGetValue(path, out var cached) && _time.GetUtcNow() < cached.Expires)
        {
            return (cached.Sha, cached.Size);
        }

        var anchor = await FetchOfficialAnchorAsync(path, token);
        if (anchor is { } value)
        {
            _anchors[path] = (value.Sha, value.Size, _time.GetUtcNow().AddMinutes(15));
        }
        return anchor;
    }

    private async Task<(string Sha, long Size)?> FetchOfficialAnchorAsync(string path, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/Estecsky/HD2-Integrated-Armor-System/contents/{path}?ref=main");
            request.Headers.UserAgent.ParseAdd("HD2Mimnt-ModManager");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            var value = sha.GetString();
            return string.IsNullOrEmpty(value) ? null : (value, size.GetInt64());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private static bool GitBlobShaMatches(byte[] content, (string Sha, long Size) meta) =>
        content.LongLength == meta.Size && ComputeGitBlobSha1(content) == meta.Sha;

    private static string ComputeGitBlobSha1(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = SHA1.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformBlock(content, 0, content.Length, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task<string> ComputeGitBlobSha1Async(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        var header = Encoding.ASCII.GetBytes($"blob {stream.Length}\0");
        using var sha = SHA1.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private async Task<T> RequestAsync<T>(string path, Func<Uri, Task<T>> request,
        IReadOnlyList<string> roots, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_time.GetUtcNow() < _retryAfter)
            throw new HttpRequestException("HD2IAS 更新请求冷却中，请稍后重试。", null, HttpStatusCode.TooManyRequests);
        Exception? last = null;
        foreach (var root in roots)
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
        if (uri.Host == OfficialHost) return;
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
