using System.Text.Json;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

internal static class AyakaModsMapper
{
    public static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    public static string Text(JsonElement value, string name) => Property(value, name).ToString();
    public static long Number(JsonElement value, string name) => long.TryParse(Text(value, name), out var result) ? result : 0;
    public static List<JsonElement> Array(JsonElement value, string name, string? alternate = null)
    {
        var array = Property(value, name);
        if (array.ValueKind == JsonValueKind.Undefined && alternate != null) array = Property(value, alternate);
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException($"A 网响应缺少 {name} 列表。");
        return array.EnumerateArray().ToList();
    }
    public static DateTime? Timestamp(JsonElement value, string name)
    {
        var number = Number(value, name);
        return number > 0 && number <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime : null;
    }
    public static string VersionId(JsonElement version) => AyakaModsApiClient.Id(
        Property(version, "mod_version_id").ValueKind != JsonValueKind.Undefined ? Text(version, "mod_version_id") : Text(version, "version_id"));

    public static PlatformModInfo Mod(JsonElement mod)
    {
        var id = AyakaModsApiClient.Id(Text(mod, "mod_id"));
        return new()
        {
            Id = id, Name = Text(mod, "title"),
            Description = Text(mod, "description") is { Length: > 0 } description ? description : Text(mod, "tag_line"),
            Author = Text(mod, "username"),
            AuthorUrl = Text(Property(mod, "User"), "view_url") is { Length: > 0 } authorUrl
                && Uri.TryCreate(authorUrl, UriKind.Absolute, out var authorUri) && authorUri.Scheme == "https"
                && authorUri.Host == "ayakamods.com" && authorUri.UserInfo.Length == 0 ? authorUri.AbsoluteUri : null,
            Version = Text(mod, "version") is { Length: > 0 } currentVersion ? currentVersion : Text(mod, "version_string"),
            UpdatedAt = Timestamp(mod, "last_update"), DownloadCount = Number(mod, "download_count"),
            ThumbnailUrl = Text(mod, "icon_url"),
            Metadata = new() { ["mod_page_url"] = $"https://ayakamods.com/mods/{id}/", ["ayaka_game_id"] = Text(mod, "game_id") }
        };
    }

    public static List<PlatformModFile> Files(IEnumerable<JsonElement> versions, string? versionFilter)
    {
        var result = new List<PlatformModFile>();
        foreach (var version in versions.OrderByDescending(v => Number(v, "release_date")))
        {
            var versionId = VersionId(version);
            var label = Text(version, "version_string");
            if (versionFilter != null && label != versionFilter) continue;
            // External-only versions are opened on the source website, never treated as ZIPs.
            var files = Property(version, "files");
            if (files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
            {
                var attachment = AyakaModsApiClient.Id(Text(file, "id"));
                result.Add(new()
                {
                    Id = $"ayaka-file:{versionId}:{attachment}", WebsiteFileId = attachment,
                    Name = Text(file, "filename"), FileName = Text(file, "filename"), Size = Number(file, "size"),
                    Version = label, UploadedAt = Timestamp(version, "release_date"), Description = Text(version, "description")
                });
            }
        }
        return result;
    }
}
