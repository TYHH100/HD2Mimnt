using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Platforms.NexusMods;
using System.Text.Json;

// Preserve the public namespace so existing callers need no migration.
namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>Nexus module entry point implementing the shared platform contract.</summary>
public class NexusModsPlatformAdapter : BasePlatformAdapter, IModFileDownloadPlatform, IModProtocolDownloadPlatform, IModBrowsePlatform, IModDownloadAccessPlatform
{
    private readonly NexusModsApiClient _api;
    private readonly NexusModsFileService _files;
    private readonly NexusModsNxmService _nxm;
    private readonly NexusModsBrowseService _browse;

    public override string Name => "NexusMods";
    public override string Id => "nexusmods";
    public override PlatformCapabilities Capabilities =>
        PlatformCapabilities.Search | PlatformCapabilities.Download |
        PlatformCapabilities.Metadata | PlatformCapabilities.Versions |
        PlatformCapabilities.Files | PlatformCapabilities.Authentication |
        PlatformCapabilities.RateLimit;

    public NexusModsPlatformAdapter(HttpClient httpClient, IHttpDownloadService? downloader = null,
        string? applicationVersion = null) : base(httpClient)
    {
        var version = applicationVersion ??
            (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(NexusModsPlatformAdapter).Assembly)
            .GetName().Version!.ToString();
        _api = new NexusModsApiClient(httpClient, version);
        var downloadService = downloader ?? new HttpDownloadService(httpClient);
        _files = new NexusModsFileService(_api, downloadService);
        _nxm = new NexusModsNxmService(_api, downloadService);
        _browse = new NexusModsBrowseService(_api);
    }

    public void SetApiKey(string apiKey) => _api.SetApiKey(apiKey);
    public void SetBearerToken(string token) => _api.SetBearerToken(token);
    public void SetOAuthAuthentication(Func<CancellationToken, Task<string>> tokenProvider) => _api.SetOAuthAuthentication(tokenProvider);
    public void ClearApiKey() => _api.ClearApiKey();
    public void ClearAuthentication() => _api.ClearAuthentication();
    public bool IsAuthenticated => _api.IsAuthenticated;
    public NexusModsRateLimit RateLimit => _api.RateLimit;
    public event EventHandler? RateLimitChanged
    {
        add => _api.RateLimitChanged += value;
        remove => _api.RateLimitChanged -= value;
    }
    public async Task RefreshRateLimitAsync(CancellationToken cancellationToken = default)
        => _ = await _api.GetLegacyDataAsync<JsonElement>("/users/validate.json", "刷新 N 网请求额度", cancellationToken);

    public Task<PlatformSearchResult> BrowseAsync(ModBrowseFeed feed, CancellationToken cancellationToken = default)
        => _browse.BrowseAsync(feed, cancellationToken);

    public Task<PlatformModInfo> GetBrowseDetailsAsync(string modId, CancellationToken cancellationToken = default)
        => _browse.GetDetailsAsync(modId, cancellationToken);

    public Task<PlatformSearchResult> SearchBrowseAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
        => _browse.SearchAsync(query, cancellationToken);

    /// <summary>Checks public API availability, not credential validity (v3 has no validation endpoint).</summary>
    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.GetTrendingModsAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
    }

    /// <summary>Filters only the public top-five trending feed.</summary>
    public override async Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        // The feed has no dates, download counts or ratings to sort by.
        if (query.SortBy is not (PlatformSearchSort.Relevance or PlatformSearchSort.Name or PlatformSearchSort.Author))
            throw new NotSupportedException("Nexus Mods v3 热门列表只支持默认、名称和作者排序。");
        if (query.Filters.Count > 0)
            throw new NotSupportedException("Nexus Mods v3 热门列表不支持额外筛选条件。");

        var feed = await _api.GetTrendingModsAsync(cancellationToken);
        return NexusModsMapper.ToSearchResult(feed, query);
    }

    public override async Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default) =>
        NexusModsMapper.ToModInfo(await _api.GetModAsync(modId, cancellationToken));

    public override async Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default) =>
        NexusModsMapper.ToFiles(await _files.GetFileVersionsAsync(modId, cancellationToken), version);

    public override async Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default) =>
        NexusModsMapper.ToVersions(await _files.GetFileVersionsAsync(modId, cancellationToken));

    public override Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _files.DownloadModAsync(modId, progress, cancellationToken);

    public Task<Stream> DownloadFileAsync(string modId, string fileVersionId,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        fileVersionId.StartsWith("nexus-v1-file:", StringComparison.Ordinal)
            ? _nxm.DownloadPremiumAsync(modId, fileVersionId, progress, cancellationToken)
            : _files.DownloadFileAsync(modId, fileVersionId, progress, cancellationToken);

    public Task<bool> HasDirectDownloadAccessAsync(CancellationToken cancellationToken = default)
        => _nxm.IsPremiumAsync(cancellationToken);

    public Task<List<PlatformModFile>> GetProtocolFilesAsync(string modId, CancellationToken cancellationToken = default)
        => _nxm.GetFilesAsync(modId, cancellationToken);
    public string GetWebsiteDownloadUrl(string modId, PlatformModFile file)
    {
        if (!NexusModsNxmLink.TryPositiveId(modId, out var mod) ||
            !NexusModsNxmLink.TryPositiveId(file.WebsiteFileId ?? "", out var id) || file.Id != "nexus-v1-file:" + id)
            throw new ArgumentException("所选文件缺少有效的网页编号，请重新检查更新。");
        return $"https://www.nexusmods.com/helldivers2/mods/{mod}?tab=files&file_id={id}&nmm=1";
    }
    public ModProtocolTarget ReadProtocolTarget(string protocolLink)
    {
        var link = NexusModsNxmLink.Parse(protocolLink);
        return new(Id, "helldivers2", link.ModId, link.FileId);
    }
    public Task<PlatformModFile> ResolveProtocolFileAsync(string modId, string protocolLink, CancellationToken cancellationToken = default)
        => _nxm.ResolveAsync(modId, protocolLink, cancellationToken);
    public Task<Stream> DownloadProtocolFileAsync(string modId, string fileId, string protocolLink,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        => _nxm.DownloadAsync(modId, fileId, protocolLink, progress, cancellationToken);
}
