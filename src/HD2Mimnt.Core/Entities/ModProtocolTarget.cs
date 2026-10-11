namespace HD2Mimnt.Core.Entities;

/// <summary>协议下载的公开目标，不含临时授权。</summary>
public sealed record ModProtocolTarget(string PlatformId, string GameDomain, string ModId, string WebsiteFileId);
