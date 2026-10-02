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
