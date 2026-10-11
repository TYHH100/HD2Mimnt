using System.Globalization;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

public static class NexusModsLinkParser
{
    public static ModPlatformLink Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.ToLowerInvariant() is not ("www.nexusmods.com" or "nexusmods.com"))
            throw new ArgumentException("当前仅支持 Nexus Mods 的模组页面链接；A 网待文档恢复后接入。");

        var path = uri.AbsolutePath.TrimEnd('/').Split('/');
        if (path.Length != 4 || !path[1].Equals("helldivers2", StringComparison.OrdinalIgnoreCase) ||
            !path[2].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(path[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("请输入 Helldivers 2 模组页面链接，例如 https://www.nexusmods.com/helldivers2/mods/16633。");

        var modId = id.ToString(CultureInfo.InvariantCulture);
        return new("nexusmods", "helldivers2", modId,
            $"https://www.nexusmods.com/helldivers2/mods/{modId}");
    }
}
