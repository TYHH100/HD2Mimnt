using System.Text.Json;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

public static partial class DarktideReleaseParser
{
    public const string RepositoryPage = "https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader";

    public static DarktideReleaseInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = ReadString(root, "tag_name") ?? "";
            var name = ReadString(root, "name") ?? tag;
            var page = ReadString(root, "html_url") ?? $"{RepositoryPage}/releases";
            var body = ReadString(root, "body") ?? "";
            DateTimeOffset? published = null;
            if (DateTimeOffset.TryParse(ReadString(root, "published_at"), out var parsed)) published = parsed;

            var assets = new List<DarktideReleasePackage>();
            if (TryGetProperty(root, "assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var fileName = ReadString(asset, "name") ?? "";
                    var url = ReadString(asset, "browser_download_url") ?? "";
                    if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsOfficialDownloadUrl(url)) continue;
                    assets.Add(new DarktideReleasePackage(Classify(fileName), fileName, url, "asset"));
                }
            }

            // GitHub Release 资产优先；当前 DML 发布 assets 为空，需要从正文 Markdown 链接读取。
            var packages = assets.Count > 0 ? assets : ParseBodyLinks(body);
            if (tag.Length == 0) return new DarktideReleaseInfo(tag, name, published, page, body, packages, "发布信息缺少版本标签。");
            if (packages.Count == 0)
                return new DarktideReleaseInfo(tag, name, published, page, body, packages,
                    "发布说明中没有找到可识别的官方 ZIP 下载链接，请前往 Release 页面手动下载。");
            foreach (var group in packages.GroupBy(package => package.Type))
            {
                if (group.Select(package => package.DownloadUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1) continue;
                return new DarktideReleaseInfo(tag, name, published, page, body, Array.Empty<DarktideReleasePackage>(),
                    "同一类型存在多个不同的官方下载链接，已停止自动安装。");
            }
            return new DarktideReleaseInfo(tag, name, published, page, body, packages, null);
        }
        catch (JsonException ex)
        {
            return new DarktideReleaseInfo("", "", null, $"{RepositoryPage}/releases", "",
                Array.Empty<DarktideReleasePackage>(), $"GitHub Release 响应不是有效 JSON：{ex.Message}");
        }
    }

    public static bool IsOfficialDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.AbsolutePath;
        return path.StartsWith("/user-attachments/files/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/download/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static List<DarktideReleasePackage> ParseBodyLinks(string body)
    {
        var packages = new List<DarktideReleasePackage>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MarkdownLinkRegex().Matches(body))
        {
            var text = match.Groups["text"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            AddBodyPackage(packages, seenUrls, text, url, "body-link");
        }
        foreach (Match match in BareZipUrlRegex().Matches(body))
            AddBodyPackage(packages, seenUrls, "", match.Groups["url"].Value.Trim(), "body-url");
        return packages;
    }

    private static void AddBodyPackage(List<DarktideReleasePackage> packages, HashSet<string> seenUrls,
        string linkText, string url, string source)
    {
        if (!IsOfficialDownloadUrl(url) || !seenUrls.Add(url)) return;
        var fileName = linkText.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? linkText
            : Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        packages.Add(new DarktideReleasePackage(Classify(fileName), fileName, url, source));
    }

    private static DarktideReleasePackageType Classify(string fileName)
        => fileName.Contains("updateonly", StringComparison.OrdinalIgnoreCase)
           || fileName.Contains("update-only", StringComparison.OrdinalIgnoreCase)
           || fileName.Contains("update only", StringComparison.OrdinalIgnoreCase)
            ? DarktideReleasePackageType.UpdateOnly
            : DarktideReleasePackageType.Full;

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\((?<url>https?://[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?<url>https?://[^\s<>()\[\]]+\.zip)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareZipUrlRegex();
}
