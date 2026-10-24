using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

public static partial class AyakaModsLinkParser
{
    public static ModPlatformLink Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.ToLowerInvariant() is not ("ayakamods.com" or "www.ayakamods.com"))
            throw new ArgumentException("请输入 Ayaka Mods 的模组页面链接。");
        var match = ModPath().Match(uri.AbsolutePath);
        if (!match.Success) throw new ArgumentException("请输入 A 网模组页面链接，例如 https://ayakamods.com/mods/example.42/。");
        var id = AyakaModsApiClient.Id(match.Groups[1].Value);
        return new("ayakamods", "helldivers2", id, $"https://ayakamods.com/mods/{id}/");
    }

    [GeneratedRegex(@"^/mods/(?:[^/]+\.)?([0-9]+)(?:/download)?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModPath();
}
