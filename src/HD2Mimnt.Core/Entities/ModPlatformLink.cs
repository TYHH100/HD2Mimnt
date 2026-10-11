namespace HD2Mimnt.Core.Entities;

/// <summary>与本地模组绑定的平台页面及 API 信息；不包含认证或临时下载地址。</summary>
public sealed record ModPlatformLink(
    string PlatformId, string GameDomain, string ModId, string Url,
    string? Name = null, string? GlobalModId = null, string? LatestVersion = null,
    DateTime? RetrievedAt = null, string? InstalledFileVersionId = null, string? InstalledWebsiteFileId = null);
