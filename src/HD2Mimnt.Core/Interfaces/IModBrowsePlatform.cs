using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public enum ModBrowseFeed { Trending, LatestAdded, LatestUpdated }

/// <summary>平台公开列表与完整详情，独立于有限范围的 SearchAsync。</summary>
public interface IModBrowsePlatform
{
    Task<PlatformSearchResult> BrowseAsync(ModBrowseFeed feed, CancellationToken cancellationToken = default);
    Task<PlatformSearchResult> SearchBrowseAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);
    Task<PlatformModInfo> GetBrowseDetailsAsync(string modId, CancellationToken cancellationToken = default);
}
