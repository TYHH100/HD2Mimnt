namespace HD2Mimnt.Core.Entities;

/// <summary>模组与当前游戏资源参考之间的兼容性状态。</summary>
public enum ModCompatibilityStatus
{
    Unknown,
    Compatible,
    Incompatible,
    Error,
    NotApplicable
}

public enum ModCompatibilityReason
{
    None,
    NoModelResources,
    GamePathNotConfigured,
    ReferenceUnavailable,
    NoComparableUnits,
    ModelDataUnreadable
}

public enum CompatibilityPatchHealth
{
    Healthy,
    Warning,
    Corrupted,
    NoUnitResources
}

public sealed class CompatibilityUnit
{
    public string FileName { get; init; } = string.Empty;
    public ulong ResourceId { get; init; }
    public string? ResourceName { get; set; }
    public uint Version { get; init; }
    public uint DataSize { get; init; }
    public uint StreamSize { get; init; }
    public uint GpuSize { get; init; }
    public uint? ReferenceVersion { get; set; }
    public GameUnitReferenceStatus? ReferenceStatus { get; set; }
    public IReadOnlyList<string> ReferencePackages { get; set; } = [];
    public CompatibilityUnitAnalysis? Analysis { get; set; }
}

public sealed class CompatibilityPatchReport
{
    public string RelativePath { get; init; } = string.Empty;
    public string? ArchiveName { get; set; }
    public long FileSize { get; init; }
    public CompatibilityPatchHealth Health { get; set; }
    /// <summary>null 表示索引未能读取，不能据此断言补丁不含模型。</summary>
    public bool? ContainsUnitResources { get; set; }
    public bool HeaderValid { get; set; }
    public bool IndexValid { get; set; }
    public bool PayloadRangesValid { get; set; }
    public bool EntryIndicesValid { get; set; } = true;
    public bool TypeDistributionValid { get; set; } = true;
    public bool RequiresStream { get; set; }
    public bool RequiresGpuResources { get; set; }
    public bool HasStream { get; set; }
    public bool HasGpuResources { get; set; }
    public int TypeCount { get; set; }
    public int ResourceCount { get; set; }
    public List<CompatibilityUnit> Units { get; init; } = [];
    public List<string> Issues { get; init; } = [];
}

public sealed class ModCompatibilityReport
{
    public Guid? ModId { get; init; }
    public string ModPath { get; init; } = string.Empty;
    public ModCompatibilityStatus Status { get; set; }
    public ModCompatibilityReason Reason { get; set; }
    public int ComparedUnitCount { get; set; }
    public uint? ReferenceVersion { get; set; }
    public string ReferenceSource { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; init; }
    public DateTime? NamesUpdatedAtUtc { get; set; }
    public string? NamesSource { get; set; }
    public string? NamesUpdateError { get; set; }
    public int UnitCount { get; set; }
    public Dictionary<uint, int> VersionCounts { get; init; } = [];
    public List<CompatibilityUnit> Units { get; init; } = [];
    public List<CompatibilityPatchReport> Patches { get; init; } = [];
    public List<string> Issues { get; init; } = [];

    /// <summary>结构诊断供修复前校验使用，不代表模型版本与游戏不匹配。</summary>
    public bool HasBlockingIssues => Units.Any(u => u.Analysis is { IsValid: false }) || Patches.Any(p => p.ContainsUnitResources != false && (p.Health == CompatibilityPatchHealth.Corrupted
        || (p.RequiresStream && !p.HasStream)
        || (p.RequiresGpuResources && !p.HasGpuResources)
        || !p.IndexValid || !p.PayloadRangesValid || !p.TypeDistributionValid || !p.EntryIndicesValid));

    /// <summary>确定损坏：补丁结构不可读（头部/索引/载荷/类型分布）或必需伴随文件缺失。
    /// 不含 entry_index、GPU 采样等仅警告级诊断，可作为角标依据而不复现历史误报。</summary>
    public bool HasBlockingStructureIssues => Patches.Any(p => p.ContainsUnitResources != false
        && (p.Health == CompatibilityPatchHealth.Corrupted
            || (p.RequiresStream && !p.HasStream)
            || (p.RequiresGpuResources && !p.HasGpuResources)));
}

public enum CompatibilityRepairKind
{
    EntryIndex,
    TypeResourceCount,
    ResourceTypeId,
    TypeAlignment,
    MainDataOffset,
    UnitDataSize
}

public sealed class CompatibilityRepairAction
{
    public CompatibilityRepairKind Kind { get; init; }
    public string PatchFilePath { get; init; } = string.Empty;
    public long Offset { get; init; }
    public int Width { get; init; }
    public ulong OldValue { get; init; }
    public ulong NewValue { get; init; }
}

public sealed class CompatibilityRepairPlan
{
    public List<CompatibilityRepairAction> Actions { get; init; } = [];
    public List<string> BlockingReasons { get; init; } = [];
    public int ActionCount => Actions.Count;
    public bool CanRepair => Actions.Count > 0 && BlockingReasons.Count == 0;
}

public sealed class CompatibilityRepairResult
{
    public bool Success { get; init; }
    public bool Changed { get; init; }
    public int AppliedActionCount { get; init; }
    public List<string> BackupPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public List<string> RecoveryErrors { get; init; } = [];
    public List<string> RecoveredPaths { get; init; } = [];
}
