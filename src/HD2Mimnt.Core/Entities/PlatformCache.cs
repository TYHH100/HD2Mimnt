namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 平台 Mod 元数据缓存
/// 减少 API 调用次数，提升性能
/// </summary>
public class PlatformCache
{
    /// <summary>
    /// 缓存条目唯一标识
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 平台标识符（如 "nexusmods", "github"）
    /// </summary>
    public string PlatformId { get; set; } = string.Empty;

    /// <summary>
    /// 平台特定的 Mod ID
    /// </summary>
    public string PlatformModId { get; set; } = string.Empty;

    /// <summary>
    /// 缓存数据（JSON 序列化的 PlatformModInfo）
    /// </summary>
    public string CachedData { get; set; } = string.Empty;

    /// <summary>
    /// 缓存时间
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// 过期时间
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// HTTP ETag（用于缓存验证）
    /// </summary>
    public string Etag { get; set; } = string.Empty;
}
