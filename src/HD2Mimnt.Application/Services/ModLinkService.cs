using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Platforms.NexusMods;

namespace HD2Mimnt.Application.Services;

public sealed class ModLinkService(IPlatformRegistry platforms, ModService mods)
{
    public ModPlatformLink Parse(string input) => NexusModsLinkParser.Parse(input);

    public async Task<ModPlatformLink> FetchAsync(string input, CancellationToken cancellationToken = default)
    {
        var link = Parse(input);
        var platform = platforms.GetPlatform(link.PlatformId)
            ?? throw new InvalidOperationException("N 网平台未注册。");
        if (platform is NexusModsPlatformAdapter nexus && !nexus.IsAuthenticated)
            throw new InvalidOperationException("请先在“设置 → 账号”中登录 N 网，或在开发工具应用个人 API Key 测试。");

        var info = await platform.GetModInfoAsync(link.ModId, cancellationToken);
        if (info.Id != link.ModId)
            throw new InvalidOperationException("API 返回的模组编号与链接不一致，未保存关联。");
        var versions = await platform.GetVersionsAsync(link.ModId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return link with
        {
            Name = string.IsNullOrWhiteSpace(info.Name) ? null : info.Name,
            GlobalModId = info.Metadata.GetValueOrDefault("nexus_global_mod_id")?.ToString(),
            LatestVersion = versions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Version))?.Version,
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
}
