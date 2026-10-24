namespace HD2Mimnt.Core.Enums;

/// <summary>
/// Archive type classification
/// </summary>
public enum ArchiveType
{
    Unknown,
    Zip,
    SevenZip,
    Rar,
    Tar,
    GZip
}

/// <summary>
/// Component type classification for logical mods
/// </summary>
public enum ComponentType
{
    Unknown,
    HD2IAS,
    Model,
    Texture,
    Material,
    Animation,
    Lua,
    Addon,
    Appearance,
    Other
}

/// <summary>
/// Detection confidence level
/// </summary>
public enum DetectionConfidence
{
    None,
    Low,
    Medium,
    High,
    Absolute
}

/// <summary>
/// Merge decision for component candidates
/// </summary>
public enum MergeDecision
{
    Unknown,
    AutomaticMerge,
    ManualConfirmation,
    SeparateImport,
    Ignore
}

/// <summary>
/// Logical mod structure version
/// </summary>
public enum LogicalModVersion
{
    V1 = 1
}
