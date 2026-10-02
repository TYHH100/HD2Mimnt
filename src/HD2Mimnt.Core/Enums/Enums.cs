namespace HD2Mimnt.Core.Enums;

/// <summary>
/// Mod 导入来源
/// </summary>
public enum ModSource
{
    Unknown,
    LocalFolder,
    LocalZip,
    Archive,
    NexusMods,
    Workshop,
    Manual
}

/// <summary>
/// Mod 类型分类
/// </summary>
public enum ModCategory
{
    Unknown,
    Armor,
    Weapon,
    Character,
    Audio,
    UI,
    Gameplay,
    Visual,
    Script,
    Mixed,
    Other
}

/// <summary>
/// Mod 状态
/// </summary>
public enum ModStatus
{
    Unknown,
    Ready,
    Installed,
    NeedsUpdate,
    Conflict,
    Error
}

/// <summary>
/// 任务优先级
/// </summary>
public enum TaskPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// 资源节点类型
/// </summary>
public enum ResourceNodeType
{
    File,
    Directory
}

/// <summary>
/// 资源引用类型
/// </summary>
public enum ReferenceType
{
    /// <summary>
    /// 直接引用（明确的文件路径引用）
    /// </summary>
    Direct,

    /// <summary>
    /// 间接引用（推断的关联关系）
    /// </summary>
    Indirect
}

/// <summary>
/// Platform capabilities flags
/// </summary>
[Flags]
public enum PlatformCapabilities
{
    None = 0,
    Search = 1 << 0,
    Download = 1 << 1,
    Metadata = 1 << 2,
    Versions = 1 << 3,
    Update = 1 << 4,
    Files = 1 << 5,
    Authentication = 1 << 6,
    RateLimit = 1 << 7
}

/// <summary>
/// Platform search sort order
/// </summary>
public enum PlatformSearchSort
{
    Relevance,
    Name,
    Author,
    UpdatedDate,
    CreatedDate,
    DownloadCount,
    Rating
}
