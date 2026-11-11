using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Mod Library 中的核心实体
/// 代表用户保存、管理的 Mod 资产
/// </summary>
public class Mod
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public ModCategory Category { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>TOC 中的资源 Type ID。null 表示尚未扫描；空列表表示未识别出资源。</summary>
    public List<ulong>? ResourceTypeIds { get; set; }
    /// <summary>null 表示尚未识别；只依据清单声明和 Lua 资源契约识别 BSL。</summary>
    public BslModRole? BslRole { get; set; }
    public int BslDetectionVersion { get; set; }
    public string WorkflowType { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public long Size { get; set; }
    public string Hash { get; set; } = string.Empty;
    /// <summary>判重指纹：排除生成身份后的作者内容指纹，导入时随 Hash 一次算出。
    /// 空表示尚未计算（旧库条目），判重时惰性回填，避免每次导入重扫整个模组库。</summary>
    public string ContentFingerprint { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public ModSource Source { get; set; }
    public ModStatus Status { get; set; }

    /// <summary>导入或更新后尚未查看的卡片提示；点击模组卡片后清除。</summary>
    public ModNoticeKind Notice { get; set; }

    /// <summary>
    /// Mod 清单数据
    /// </summary>
    public ModManifest? Manifest { get; set; }

    /// <summary>Application 从独立组件元数据加载，数据库仍使用统一的 Mod/Profile 身份。</summary>
    public LogicalMod? LogicalMod { get; set; }

    /// <summary>
    /// 是否包含脚本（需要安全审核）
    /// </summary>
    public bool ContainsScripts { get; set; }

    /// <summary>
    /// Nexus Mods ID（如果来自 Nexus）
    /// </summary>
    public int? NexusModId { get; set; }

    /// <summary>
    /// Platform identifier (e.g., "nexusmods", "github")
    /// </summary>
    public string? PlatformId { get; set; }

    /// <summary>
    /// Platform-specific mod identifier
    /// </summary>
    public string? PlatformModId { get; set; }

    /// <summary>
    /// Platform version string
    /// </summary>
    public string? PlatformVersion { get; set; }

    public ModPlatformLink? PlatformLink { get; set; }

    /// <summary>
    /// Last time update was checked
    /// </summary>
    public DateTime? LastUpdateCheck { get; set; }
}
