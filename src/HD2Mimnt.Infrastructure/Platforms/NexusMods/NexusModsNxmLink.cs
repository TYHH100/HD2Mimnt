using System.Globalization;
using System.Net;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

internal sealed class NexusModsNxmLink
{
    public required string ModId { get; init; }
    public required string FileId { get; init; }
    public required string Key { get; init; }
    public required long Expires { get; init; }
    public required long UserId { get; init; }
    public string DownloadId => "nexus-v1-file:" + FileId;
    public override string ToString() => $"Nexus Mods · {ModId} · {FileId}";

    public static NexusModsNxmLink Parse(string input, string? expectedModId = null)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "nxm" ||
            uri.Authority != "helldivers2" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("收到的链接不是 N 网 Helldivers 2 临时下载链接。");
        var path = uri.AbsolutePath.Trim('/').Split('/');
        if (path.Length != 4 || path[0] != "mods" || path[2] != "files" ||
            !TryPositiveId(path[1], out var mod) || !TryPositiveId(path[3], out var file))
            throw new ArgumentException("临时链接必须包含有效的模组编号和文件编号。");
        if (expectedModId != null && mod != expectedModId) throw new ArgumentException("临时链接属于另一个模组，请从当前模组网页重新获取。");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !query.TryAdd(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair[1])))
                throw new ArgumentException("临时链接参数不完整或重复，请重新从网页复制。");
        }
        if (!query.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl) ||
            !query.TryGetValue("expires", out var expiry) || !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            !query.TryGetValue("user_id", out var account) || !long.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            throw new ArgumentException("免费账户链接需包含 key、expires 和 user_id，请在网页点击“Mod manager download”重新获取完整链接。");
        if (expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new InvalidOperationException("临时下载链接已过期，请在 N 网网页重新点击“Mod manager download”。");
        return new() { ModId = mod, FileId = file, Key = key, Expires = expires, UserId = userId };
    }

    internal static bool TryPositiveId(string input, out string result)
    {
        result = "";
        if (input.Length == 0 || !input.All(char.IsAsciiDigit) || !long.TryParse(input, out var value) || value <= 0) return false;
        result = value.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
