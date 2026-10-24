using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.UI.Services;

/// <summary>公开应用配置。获批后将 ClientId 填在这里，无需在桌面应用保存 Client Secret。</summary>
public static class NexusModsOAuthConfiguration
{
    public const string ClientId = "";
    public static NexusModsOAuthOptions Create() => new()
    {
        ClientId = Environment.GetEnvironmentVariable("HD2MIMNT_NEXUS_CLIENT_ID") ?? ClientId,
        RedirectUri = Environment.GetEnvironmentVariable("HD2MIMNT_NEXUS_REDIRECT_URI") ?? NexusModsOAuthOptions.DefaultRedirectUri
    };
}
