using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;
using System.Text.Json;
using static HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsApiClient;
using static HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsFileSelector;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>Maps Nexus-specific data to the platform-neutral Core models.</summary>
internal static class NexusModsMapper
{
    public static PlatformSearchResult ToSearchResult(TrendingModsResponse feed, PlatformSearchQuery query)
    {
        var filtered = feed.Mods
            .Where(m => string.IsNullOrWhiteSpace(query.Query) ||
                m.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase) ||
                (m.Summary?.Contains(query.Query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(m => new PlatformModInfo
            {
                Id = GetGameScopedId(m.ModPageUrl),
                Name = m.Name,
                Author = m.Author ?? string.Empty,
                Description = m.Summary ?? string.Empty,
                ThumbnailUrl = m.PictureUrl ?? string.Empty,
                Metadata = new() { ["mod_page_url"] = m.ModPageUrl, ["search_scope"] = "trending" }
            }).ToList();
        filtered = query.SortBy switch
        {
            PlatformSearchSort.Name => filtered.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            PlatformSearchSort.Author => filtered.OrderBy(m => m.Author, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => filtered
        };
        var skip = ((long)query.Page - 1) * query.PageSize;
        return new PlatformSearchResult
        {
            Mods = skip >= filtered.Count ? new() : filtered.Skip((int)skip).Take(query.PageSize).ToList(),
            TotalCount = filtered.Count,
            Page = query.Page,
            PageSize = query.PageSize,
            ScopeDescription = "仅在 Nexus Mods 热门前 5 项中筛选"
        };
    }

    public static PlatformModInfo ToModInfo(NexusMod mod)
    {
        // v3 Mod contains only IDs and nullable name. Do not invent v1 metadata.
        return new PlatformModInfo
        {
            Id = mod.GameScopedId,
            Name = mod.Name ?? string.Empty,
            Metadata = new()
            {
                ["nexus_mod_id"] = mod.GameScopedId,
                ["nexus_global_mod_id"] = mod.Id,
                ["game_id"] = mod.GameId,
                ["mod_page_url"] = $"https://www.nexusmods.com/{GameDomain}/mods/{Uri.EscapeDataString(mod.GameScopedId)}"
            }
        };
    }

    public static List<PlatformModFile> ToFiles(List<FileVersion> versions, string? version)
    {
        var preferred = SelectDownloadVersion(versions);
        return versions.Where(IsDownloadable).Where(v => string.IsNullOrWhiteSpace(version) || v.Version == version)
            .OrderByDescending(v => v.Id == preferred?.Id).ThenByDescending(v => v.UploadedAt)
            .Select(v => new PlatformModFile
            {
                // Downloads address the global version ID, not ModFile.id or game_scoped_id.
                Id = v.Id,
                Name = v.Name,
                Version = v.Version,
                UploadedAt = v.UploadedAt
                // The schema has no archive size, description or permanent download URL.
            }).ToList();
    }

    public static List<PlatformModVersion> ToVersions(List<FileVersion> versions)
    {
        var downloadable = versions.Where(IsDownloadable).ToList();
        var preferred = SelectDownloadVersion(downloadable);
        // Preferred download comes first so update checking does not mistake an optional file for the main version.
        return downloadable.GroupBy(v => v.Version)
            .Select(g => new PlatformModVersion { Version = g.Key, ReleasedAt = g.Max(v => v.UploadedAt) })
            .OrderByDescending(v => v.Version == preferred?.Version)
            .ThenByDescending(v => v.ReleasedAt).ToList();
    }

    private static string GetGameScopedId(string pageUrl)
    {
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host is "www.nexusmods.com" or "nexusmods.com")
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length == 3 && segments[0] == GameDomain && segments[1] == "mods" &&
                segments[2].Length > 0 && segments[2].All(char.IsAsciiDigit))
                return segments[2];
        }
        throw new JsonException("热门模组缺少有效的 Helldivers 2 Nexus 页面地址。");
    }
}
