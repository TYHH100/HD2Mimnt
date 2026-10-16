using System.Text.Json;
using System.Collections.Concurrent;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;
using Microsoft.Extensions.Logging;
using static HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsMapper;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

/// <summary>Read-only Ayaka tool integration. Provider identities and HTTP details stay in this module.</summary>
public sealed class AyakaModsPlatformAdapter : BasePlatformAdapter, IModBrowsePlatform,
    IModFileDownloadPlatform, IModDownloadAccessPlatform, IDisposable
{
    public const string Helldivers2GameId = "119";
    private readonly AyakaModsApiClient _api;
    private readonly HttpClient _downloadClient;
    private readonly IHttpDownloadService _downloads;
    private readonly IDownloadOptionsProvider? _options;
    // Cache only mapped public metadata: no version DTOs, raw storage URLs or credentials.
    private readonly ConcurrentDictionary<string, (DateTime Expires, int Revision, object Value)> _cache = new();
    public override string Id => "ayakamods";
    public override string Name => "Ayaka Mods";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Metadata | PlatformCapabilities.Versions |
        PlatformCapabilities.Files | PlatformCapabilities.Download | PlatformCapabilities.Authentication;
    public bool IsAuthenticated => _api.IsAuthenticated;
    public static string ValidateApiKey(string value) => AyakaModsApiClient.ValidateKey(value);

    public AyakaModsPlatformAdapter(HttpClient apiClient, IDownloadOptionsProvider? options = null,
        string? resumeDirectory = null, string? tempDirectory = null, HttpMessageHandler? downloadTransport = null,
        ILogger<AyakaModsPlatformAdapter>? logger = null) : base(apiClient)
    {
        _api = new(apiClient, logger);
        _options = options;
        _downloadClient = new(new DirectDownloadResponseGuard(new AyakaModsDownloadHandler(() => _api.ApiKey,
            downloadTransport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, logger)));
        _downloads = new HttpDownloadService(_downloadClient, options, resumeDirectory, tempDirectory);
    }

    public void SetApiKey(string value) { _api.SetApiKey(value); _cache.Clear(); }
    public void ClearApiKey() { _api.ClearApiKey(); _cache.Clear(); }

    private T? Cached<T>(string key, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested(); _api.EnsureAuthenticated();
        return _cache.TryGetValue(key, out var entry) && entry.Revision == _api.Revision && entry.Expires > DateTime.UtcNow
            ? entry.Value as T : null;
    }
    private T Remember<T>(string key, T value, int revision) where T : class
    {
        _api.EnsureRevision(revision);
        // Bound a browsing session's memory even if many pages are visited.
        if (_cache.Count >= 128) _cache.Clear();
        _cache[key] = (DateTime.UtcNow.AddMinutes(2), revision, value);
        return value;
    }

    private Task<string> GetGameIdAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _api.EnsureAuthenticated();
        return Task.FromResult(Helldivers2GameId);
    }

    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return false;
        try { _ = await _api.GetAsync($"mod-games/{Helldivers2GameId}/", cancellationToken); return true; }
        catch (HttpRequestException) { return false; }
    }

    public Task<bool> HasDirectDownloadAccessAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); _api.EnsureAuthenticated(); return Task.FromResult(true); }

    public Task<PlatformSearchResult> BrowseAsync(ModBrowseFeed feed, CancellationToken cancellationToken = default)
        => BrowsePageAsync(feed, 1, cancellationToken);

    public async Task<PlatformSearchResult> BrowsePageAsync(ModBrowseFeed feed, int page, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var cacheKey = $"list:{feed}:{page}";
        if (Cached<PlatformSearchResult>(cacheKey, token) is { } cached) return cached;
        var revision = _api.Revision;
        var game = await GetGameIdAsync(token);
        var order = feed switch { ModBrowseFeed.LatestAdded => "mod_date", ModBrowseFeed.LatestUpdated => "last_update", _ => "download_count" };
        var response = await _api.GetAsync($"mod-games/{game}/mods/?order={order}&direction=desc&page={page}", token);
        var mods = Array(response, "mods");
        if (mods.Any(mod => Text(mod, "game_id") != game)) throw new JsonException("A 网列表包含其它游戏，已拒绝显示。");
        var pagination = Property(response, "pagination");
        var current = checked((int)Number(pagination, "current_page"));
        var size = checked((int)Number(pagination, "per_page"));
        var total = checked((int)Number(pagination, "total"));
        if (current != page || size < 1 || total < 0) throw new JsonException("A 网分页信息无效。");
        return Remember(cacheKey, new PlatformSearchResult { Mods = mods.Select(Mod).ToList(), Page = current, PageSize = size, TotalCount = total,
            ScopeDescription = $"Ayaka Mods · Helldivers 2 · {total} · {current}" }, revision);
    }

    public override Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
        => SearchBrowseAsync(query, cancellationToken);

    public Task<PlatformSearchResult> SearchBrowseAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A 网公开 API 暂未提供关键词搜索，请使用列表或模组链接。");

    public async Task<PlatformModInfo> GetBrowseDetailsAsync(string modId, CancellationToken cancellationToken = default)
    {
        var id = AyakaModsApiClient.Id(modId);
        if (Cached<PlatformModInfo>("mod:" + id, cancellationToken) is { } cached) return cached;
        var revision = _api.Revision;
        return Remember("mod:" + id, Mod(await GetModAsync(id, cancellationToken)), revision);
    }
    public override Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default)
        => GetBrowseDetailsAsync(modId, cancellationToken);

    private async Task<JsonElement> GetModAsync(string modId, CancellationToken token)
    {
        var id = AyakaModsApiClient.Id(modId);
        var game = await GetGameIdAsync(token);
        var mod = Property(await _api.GetAsync($"mods/{id}/", token), "mod");
        if (Text(mod, "mod_id") != id || Text(mod, "game_id") != game)
            throw new JsonException("A 网返回的模组编号或游戏与请求不一致。");
        return mod;
    }

    private async Task<List<JsonElement>> VersionsAsync(string modId, CancellationToken token)
    {
        var id = AyakaModsApiClient.Id(modId);
        _ = await GetModAsync(id, token);
        var root = await _api.GetAsync($"mods/{id}/versions/", token);
        var versions = Array(root, "versions", "mod_versions");
        if (versions.Any(v => Property(v, "mod_id").ValueKind != JsonValueKind.Undefined && Text(v, "mod_id") != id))
            throw new JsonException("A 网版本列表包含其它模组。");
        return versions;
    }

    public override async Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default)
        => Files(await VersionsAsync(modId, cancellationToken), version);
    public override async Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default)
        => (await VersionsAsync(modId, cancellationToken)).OrderByDescending(v => Number(v, "release_date")).Select(v => new PlatformModVersion
        { Version = Text(v, "version_string"), ReleasedAt = Timestamp(v, "release_date") ?? default, ChangeLog = Text(v, "description") }).ToList();

    public override async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var file = (await GetFilesAsync(modId, cancellationToken: cancellationToken)).FirstOrDefault()
            ?? throw new InvalidOperationException("A 网没有可直接下载的托管文件，请查看来源网页。");
        return await DownloadFileAsync(modId, file.Id, progress, cancellationToken);
    }

    public async Task<Stream> DownloadFileAsync(string modId, string fileVersionId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Refresh ownership and visibility rather than trusting a previously displayed URL or client-supplied ID.
        var files = await GetFilesAsync(modId, cancellationToken: cancellationToken);
        if (!files.Any(file => file.Id == fileVersionId)) throw new InvalidOperationException("所选 A 网文件已不可用，请重新检查。");
        var parts = fileVersionId.Split(':');
        var uri = new Uri(AyakaModsApiClient.BaseUri, $"mod-versions/{parts[1]}/download?file={parts[2]}");
        var configured = _options == null ? new DownloadOptions() : await _options.GetOptionsAsync();
        // Ayaka's fair-use guidance takes precedence over a user's global parallel segment setting.
        var options = new DownloadOptions { MaxParallelSegments = 1, MinimumSegmentSize = configured.MinimumSegmentSize,
            MaxRetries = configured.MaxRetries, RetryDelay = configured.RetryDelay,
            EnableResume = configured.EnableResume, OverwriteExisting = configured.OverwriteExisting };
        return await _downloads.DownloadAsync(uri, options, progress, cancellationToken, resumeKey: $"ayakamods:{modId}:{fileVersionId}");
    }

    public void Dispose() { _downloadClient.Dispose(); _api.Dispose(); }
}
