using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Platforms.NexusMods;
using System.Text.Json;

// Preserve the public namespace so existing callers need no migration.
namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>Nexus module entry point implementing the shared platform contract.</summary>
public class NexusModsPlatformAdapter : BasePlatformAdapter
{
    private readonly NexusModsApiClient _api;
    private readonly NexusModsFileService _files;

    public override string Name => "NexusMods";
    public override string Id => "nexusmods";
    public override PlatformCapabilities Capabilities =>
        PlatformCapabilities.Search | PlatformCapabilities.Download |
        PlatformCapabilities.Metadata | PlatformCapabilities.Versions |
        PlatformCapabilities.Files | PlatformCapabilities.Authentication |
        PlatformCapabilities.RateLimit;

    public NexusModsPlatformAdapter(HttpClient httpClient, IHttpDownloadService? downloader = null) : base(httpClient)
    {
        _api = new NexusModsApiClient(httpClient);
        _files = new NexusModsFileService(_api, downloader ?? new HttpDownloadService(httpClient));
    }

    public void SetApiKey(string apiKey) => _api.SetApiKey(apiKey);
    public void SetBearerToken(string token) => _api.SetBearerToken(token);
    public void ClearApiKey() => _api.ClearApiKey();
    public void ClearAuthentication() => _api.ClearAuthentication();
    public bool IsAuthenticated => _api.IsAuthenticated;

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
}
