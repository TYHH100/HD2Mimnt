using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>v1 网页文件编号和 nxm 临时授权；不混用 v3 全局文件版本 ID。</summary>
internal sealed class NexusModsNxmService(NexusModsApiClient api, IHttpDownloadService downloader)
{
    public async Task<List<PlatformModFile>> GetFilesAsync(string modId, CancellationToken token)
    {
        modId = ValidateModId(modId);
        var result = await api.GetLegacyDataAsync<LegacyFileList>($"/games/helldivers2/mods/{modId}/files.json", "获取网页文件列表", token);
        return result.Files.Where(file => file.CategoryId is 1 or 2 or 3 or 5)
            .OrderByDescending(file => file.CategoryId == 1).ThenByDescending(file => file.UploadedTimestamp)
            .Select(Map).ToList();
    }

    public async Task<bool> IsPremiumAsync(CancellationToken token)
    {
        var user = await api.GetLegacyDataAsync<LegacyUser>("/users/validate.json", "检查 N 网会员状态", token);
        if (user.UserId <= 0 || user.IsPremium == null)
            throw new JsonException("N 网未返回有效的账户会员状态。");
        return user.IsPremium.Value;
    }

    public async Task<Stream> DownloadPremiumAsync(string modId, string fileId,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        modId = ValidateModId(modId);
        const string prefix = "nexus-v1-file:";
        if (!fileId.StartsWith(prefix, StringComparison.Ordinal) ||
            !NexusModsNxmLink.TryPositiveId(fileId[prefix.Length..], out var websiteId))
            throw new ArgumentException("所选文件缺少有效的网页编号，请重新检查更新。");
        if (!await IsPremiumAsync(token))
            throw new InvalidOperationException("当前账户没有 Premium 下载权限，请重新点击下载以使用网页授权。");
        await GetFileAsync(modId, websiteId, token);
        var urls = await api.GetLegacyDataAsync<List<LegacyDownload>>(
            $"/games/helldivers2/mods/{modId}/files/{websiteId}/download_link.json", "获取会员下载链接", token);
        if (!Uri.TryCreate(urls.FirstOrDefault()?.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new JsonException("N 网未返回有效的 HTTPS 下载地址，请重新尝试下载。");
        return await downloader.DownloadAsync(uri, progress: progress, cancellationToken: token,
            resumeKey: "nexusmods:websitefile:" + websiteId);
    }

    public async Task<PlatformModFile> ResolveAsync(string modId, string input, CancellationToken token)
    {
        var link = NexusModsNxmLink.Parse(input, ValidateModId(modId));
        await ValidateAccountAsync(link, token);
        return await GetFileAsync(link, token);
    }

    public async Task<Stream> DownloadAsync(string modId, string fileId, string input,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var link = NexusModsNxmLink.Parse(input, ValidateModId(modId));
        if (fileId != link.DownloadId)
            throw new ArgumentException("临时授权的文件与所选文件不同，请重新选择或获取对应文件的 nxm 链接。");
        await ValidateAccountAsync(link, token);
        await GetFileAsync(link, token);
        // 网络等待期间也可能到期，发出授权请求前再次检查。
        NexusModsNxmLink.Parse(input, modId);
        var urls = await api.GetLegacyDataAsync<List<LegacyDownload>>(
            $"/games/helldivers2/mods/{link.ModId}/files/{link.FileId}/download_link.json?key={Uri.EscapeDataString(link.Key)}&expires={link.Expires.ToString(CultureInfo.InvariantCulture)}",
            "获取网页授权下载链接", token, link.Key);
        if (!Uri.TryCreate(urls.FirstOrDefault()?.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new JsonException("N 网未返回有效的 HTTPS 下载地址，请重新获取临时授权。");
        return await downloader.DownloadAsync(uri, progress: progress, cancellationToken: token,
            resumeKey: "nexusmods:websitefile:" + link.FileId);
    }

    private async Task ValidateAccountAsync(NexusModsNxmLink link, CancellationToken token)
    {
        if (!api.IsAuthenticated)
            throw new InvalidOperationException("nxm 链接已包含此文件的下载授权，无需再次网页授权。N 网 API 仍要求账号认证，请先在“设置 → 账号”登录 N 网，再打开此链接。");
        var user = await api.GetLegacyDataAsync<LegacyUser>("/users/validate.json", "验证临时授权所属账户", token);
        if (user.UserId != link.UserId)
            throw new InvalidOperationException("临时链接的 N 网账户与当前认证账户不同。请用同一账户登录网页并重新获取链接，或重新登录 N 网。");
    }

    private async Task<PlatformModFile> GetFileAsync(NexusModsNxmLink link, CancellationToken token)
        => await GetFileAsync(link.ModId, link.FileId, token);

    private async Task<PlatformModFile> GetFileAsync(string modId, string fileId, CancellationToken token)
    {
        var file = await api.GetLegacyDataAsync<LegacyFile>($"/games/helldivers2/mods/{modId}/files/{fileId}.json", "获取所选文件信息", token);
        if (file.FileId.ToString(CultureInfo.InvariantCulture) != fileId || file.CategoryId is 6 or 7)
            throw new InvalidOperationException("临时链接对应文件已删除、归档或返回的编号不一致，请从网页重新选择。");
        return Map(file);
    }

    private static string ValidateModId(string modId) => NexusModsNxmLink.TryPositiveId(modId, out var value)
        ? value : throw new ArgumentException("N 网模组编号无效。");

    private static PlatformModFile Map(LegacyFile file)
    {
        if (file.FileId <= 0) throw new JsonException("N 网文件信息缺少有效编号。");
        var id = file.FileId.ToString(CultureInfo.InvariantCulture);
        return new() { Id = "nexus-v1-file:" + id, WebsiteFileId = id, Name = file.Name ?? id,
            FileName = string.IsNullOrWhiteSpace(file.FileName) ? null : Path.GetFileName(file.FileName),
            Version = file.Version ?? "", Size = file.SizeKb is >= 0 and <= long.MaxValue / 1024 ? file.SizeKb * 1024 : 0 };
    }

    private sealed class LegacyFileList { public required List<LegacyFile> Files { get; init; } }
    private sealed class LegacyUser { public long UserId { get; init; } public bool? IsPremium { get; init; } }
    private sealed class LegacyFile
    {
        public long FileId { get; init; }
        public int CategoryId { get; init; }
        public string? Name { get; init; }
        public string? FileName { get; init; }
        public string? Version { get; init; }
        public long SizeKb { get; init; }
        public long UploadedTimestamp { get; init; }
    }
    private sealed class LegacyDownload { [JsonPropertyName("URI")] public string? Uri { get; init; } }
}
