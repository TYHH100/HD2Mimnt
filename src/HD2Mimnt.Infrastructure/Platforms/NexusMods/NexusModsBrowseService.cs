using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

internal sealed class NexusModsBrowseService(NexusModsApiClient api)
{
    private const string SearchQuery = """
        query SearchMods($filter: ModsFilter, $count: Int, $offset: Int) {
          mods(filter: $filter, count: $count, offset: $offset) {
            totalCount nodes { modId gameId name author summary pictureUrl version }
          }
        }
        """;

    public async Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Query.Trim();
        if (text.Length == 0) throw new ArgumentException("请输入模组名或作者后搜索。");
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        if (query.PageSize is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        var result = await api.SearchGraphAsync<SearchResponse>(SearchQuery, new
        {
            filter = new
            {
                op = "AND", gameDomainName = new[] { new { op = "EQUALS", value = "helldivers2" } },
                filter = new[] { new { op = "OR", name = new[] { new { op = "WILDCARD", value = text } },
                    author = new[] { new { op = "WILDCARD", value = text } } } }
            },
            count = query.PageSize, offset = checked((query.Page - 1) * query.PageSize)
        }, token);
        if (result.Errors?.Count > 0) throw new JsonException("N 网搜索失败，请稍后重试。");
        var page = result.Data?.Mods ?? throw new JsonException("N 网搜索响应缺少结果。");
        if (page.TotalCount < 0 || page.Nodes == null || page.Nodes.Any(mod => mod.GameId != 6119 || mod.ModId <= 0))
            throw new JsonException("N 网搜索返回的模组信息无效。");
        return new()
        {
            Page = query.Page, PageSize = query.PageSize, TotalCount = page.TotalCount,
            Mods = page.Nodes.Select(mod => Map(new BrowseMod
            {
                ModId = mod.ModId, Name = mod.Name, Author = mod.Author, Summary = mod.Summary,
                PictureUrl = mod.PictureUrl, Version = mod.Version
            })).ToList()
        };
    }

    private sealed class SearchResponse
    {
        public SearchData? Data { get; init; }
        public List<JsonElement>? Errors { get; init; }
    }
    private sealed class SearchData { public SearchPage? Mods { get; init; } }
    private sealed class SearchPage
    {
        [JsonPropertyName("totalCount")] public int TotalCount { get; init; }
        public List<SearchMod>? Nodes { get; init; }
    }
    private sealed class SearchMod
    {
        [JsonPropertyName("modId")] public long ModId { get; init; }
        [JsonPropertyName("gameId")] public int GameId { get; init; }
        public string? Name { get; init; }
        public string? Author { get; init; }
        public string? Summary { get; init; }
        [JsonPropertyName("pictureUrl")] public string? PictureUrl { get; init; }
        public string? Version { get; init; }
    }
    public async Task<PlatformSearchResult> BrowseAsync(ModBrowseFeed feed, CancellationToken token)
    {
        if (feed == ModBrowseFeed.Trending)
            return NexusModsMapper.ToSearchResult(await api.GetTrendingModsAsync(token), new PlatformSearchQuery { PageSize = 5 });
        var endpoint = feed switch
        {
            ModBrowseFeed.LatestAdded => "latest_added",
            ModBrowseFeed.LatestUpdated => "latest_updated",
            _ => throw new ArgumentOutOfRangeException(nameof(feed))
        };
        var entries = await api.GetLegacyDataAsync<List<BrowseMod>>(
            $"/games/helldivers2/mods/{endpoint}.json", "浏览 N 网模组", token);
        var mods = entries.Select(Map).ToList();
        return new() { Mods = mods, TotalCount = mods.Count, Page = 1, PageSize = 10,
            ScopeDescription = feed == ModBrowseFeed.LatestAdded ? "N 网最新发布 10 项。" : "N 网最近更新 10 项。" };
    }

    public async Task<PlatformModInfo> GetDetailsAsync(string modId, CancellationToken token)
    {
        if (!long.TryParse(modId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("N 网模组编号无效。");
        var entry = await api.GetLegacyDataAsync<BrowseMod>($"/games/helldivers2/mods/{id}.json", "获取模组详情", token);
        if (entry.ModId != id) throw new JsonException("N 网返回的模组编号与请求不一致。");
        return Map(entry);
    }

    private static PlatformModInfo Map(BrowseMod mod)
    {
        if (mod.ModId <= 0) throw new JsonException("N 网模组信息缺少有效的页面编号。");
        var id = mod.ModId.ToString(CultureInfo.InvariantCulture);
        return new()
        {
            Id = id, Name = mod.Name ?? "", Author = mod.Author ?? "", Version = mod.Version ?? "",
            Description = PlainText(mod.Description ?? mod.Summary ?? ""), ThumbnailUrl = mod.PictureUrl ?? "",
            UpdatedAt = mod.UpdatedTimestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(mod.UpdatedTimestamp).UtcDateTime : null,
            Metadata = new() { ["mod_page_url"] = $"https://www.nexusmods.com/helldivers2/mods/{id}" }
        };
    }

    private static string PlainText(string text) => WebUtility.HtmlDecode(
        Regex.Replace(Regex.Replace(text, "<(?:br|/p|/div)\\s*/?>|\\[/(?:p|list|quote)\\]", "\n", RegexOptions.IgnoreCase),
            "<[^>]*>|\\[/?[a-z][^\\]]*\\]", "", RegexOptions.IgnoreCase)).Trim();

    private sealed class BrowseMod
    {
        public long ModId { get; init; }
        public string? Name { get; init; }
        public string? Author { get; init; }
        public string? Summary { get; init; }
        public string? Description { get; init; }
        public string? PictureUrl { get; init; }
        public string? Version { get; init; }
        public long UpdatedTimestamp { get; init; }
    }
}
