using System.Text.Json;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Updates;

/// <summary>
/// 从 GitHub Releases API 读取 HD2Mimnt 自身的最新发布版本；
/// 结果缓存 15 分钟并尊重限流 Retry-After，避免启动检查耗尽匿名配额。
/// </summary>
public sealed class GitHubAppUpdateClient(HttpClient http, TimeProvider? time = null) : IAppUpdateReleaseClient
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/TYHH100/HD2Mimnt/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = http;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (AppUpdateRelease? Release, DateTimeOffset Expires) _cache;
    private DateTimeOffset _retryAfter;

    public async Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.Release is { } cached && _time.GetUtcNow() < _cache.Expires) return cached;
            if (_time.GetUtcNow() < _retryAfter) return _cache.Release;

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("HD2Mimnt-ModManager");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var until = response.Headers.RetryAfter?.Date ?? _time.GetUtcNow() +
                    (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                if (until > _retryAfter) _retryAfter = until;
            }
            response.EnsureSuccessStatusCode();

            var release = Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _cache = (release, _time.GetUtcNow().Add(CacheDuration));
            return release;
        }
        finally { _gate.Release(); }
    }

    internal static AppUpdateRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var assets = new List<AppUpdateReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object ||
                    !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                assets.Add(new AppUpdateReleaseAsset(name.GetString()!, url.GetString()!));
            }
        }

        return new AppUpdateRelease(
            tag.GetString()!,
            root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.ValueKind == JsonValueKind.String ? htmlUrl.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("published_at", out var publishedAt) && publishedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedAt.GetString(), out var published) ? published : null,
            assets);
    }
}
