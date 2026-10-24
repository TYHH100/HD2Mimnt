using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Platforms.NexusMods;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;

namespace HD2Mimnt.Application.Services;

public sealed class ModLinkService(IPlatformRegistry platforms, ModService mods)
{
    public ModPlatformLink Parse(string input)
    {
        if (input.Contains("ayakamods.com", StringComparison.OrdinalIgnoreCase)) return AyakaModsLinkParser.Parse(input);
        return NexusModsLinkParser.Parse(input);
    }

    public async Task<ModPlatformLink> FetchAsync(string input, CancellationToken cancellationToken = default)
    {
        var link = Parse(input);
        var platform = platforms.GetPlatform(link.PlatformId)
            ?? throw new InvalidOperationException("所选模组平台未注册。");
        if (platform is NexusModsPlatformAdapter nexus && !nexus.IsAuthenticated)
            throw new InvalidOperationException("请先在“设置 → 账号”中登录 N 网，或在开发工具应用个人 API Key 测试。");
        if (platform is AyakaModsPlatformAdapter ayaka && !ayaka.IsAuthenticated)
            throw new InvalidOperationException("请先在“工具 → 开发工具”保存并应用 A 网 API Key。");

        var info = await platform.GetModInfoAsync(link.ModId, cancellationToken);
        if (info.Id != link.ModId)
            throw new InvalidOperationException("API 返回的模组编号与链接不一致，未保存关联。");
        if (string.IsNullOrWhiteSpace(info.Author) && platform is IModBrowsePlatform browser)
        {
            var details = await browser.GetBrowseDetailsAsync(link.ModId, cancellationToken);
            if (details.Id != link.ModId) throw new InvalidOperationException("API 返回的模组编号与链接不一致，未保存关联。");
            info.Author = details.Author;
            info.AuthorUrl = details.AuthorUrl;
        }
        var versions = await platform.GetVersionsAsync(link.ModId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return link with
        {
            Name = string.IsNullOrWhiteSpace(info.Name) ? null : info.Name,
            GlobalModId = info.Metadata.GetValueOrDefault("nexus_global_mod_id")?.ToString(),
            LatestVersion = versions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Version))?.Version,
            Author = string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
            AuthorUrl = info.AuthorUrl,
            RetrievedAt = DateTime.UtcNow
        };
    }

    public Task<Mod> SaveAsync(Guid modId, ModPlatformLink link, CancellationToken cancellationToken = default)
    {
        var parsed = Parse(link.Url);
        if (parsed.PlatformId != link.PlatformId || parsed.GameDomain != link.GameDomain || parsed.ModId != link.ModId)
            throw new ArgumentException("链接与平台信息不一致，请重新获取。");
        return mods.SetPlatformLinkAsync(modId, link, cancellationToken);
    }

    public async Task<Mod> SaveAuthorPageAsync(Guid modId, string input, CancellationToken token = default)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 作者主页链接。");
        var mod = await mods.GetModByIdAsync(modId, token) ?? throw new KeyNotFoundException("模组已删除。");
        var link = mod.PlatformLink ?? new ModPlatformLink("website", "", "", uri.AbsoluteUri);
        if (!link.SupportsUpdates) link = link with { Url = uri.AbsoluteUri };
        return await mods.SetPlatformLinkAsync(modId, link with { AuthorUrl = uri.AbsoluteUri }, token);
    }
}
