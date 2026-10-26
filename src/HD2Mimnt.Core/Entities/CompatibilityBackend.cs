namespace HD2Mimnt.Core.Entities;

public sealed class CompatibilityUnitAnalysis
{
    public bool HeaderValid { get; set; }
    public uint? ExpectedDataSize { get; set; }
    public bool SizeMatches { get; set; }
    public bool LodValid { get; set; }
    public uint LodOffset { get; set; }
    public uint LodEnd { get; set; }
    public bool LayoutChecked { get; set; }
    public bool GpuChecked { get; set; }
    public int UnknownGpuComponents { get; set; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsValid => HeaderValid && SizeMatches && LodValid && Errors.Count == 0;

    /// <summary>"GPU 采样"类错误是对未修改 GPU 载荷的启发式抽查（首顶点位置/权重/索引采样，
    /// 2026-10-18 起不参与兼容判定），反映作者网格数据特征而非迁移或结构损坏，
    /// 不作为修复复检的阻断条件；其余错误与结构有效性一起构成阻断依据。</summary>
    public const string SamplingErrorPrefix = "GPU 采样";

    public bool HasStructuralErrors => !HeaderValid || !SizeMatches || !LodValid
        || Errors.Any(error => !error.StartsWith(SamplingErrorPrefix, StringComparison.Ordinal));
}

public enum CompatibilityLodStrategy { PreserveMod, UseGameReference, Automatic, Mixed }

public sealed class CompatibilityRepairOptions
{
    public bool RepairMetadata { get; init; } = true;
    public bool RecoverCompanions { get; init; } = true;
    public bool RepairUnits { get; init; } = true;
    public bool MigrateMaterials { get; init; } = true;
    public CompatibilityLodStrategy LodStrategy { get; init; } = CompatibilityLodStrategy.Automatic;
    public IReadOnlySet<ulong> PreserveModLodUnitIds { get; init; } = new HashSet<ulong>();
}

public sealed class CompatibilityContentAction
{
    public string RelativePath { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public ulong? ResourceId { get; init; }
    public uint? OldVersion { get; init; }
    public uint? NewVersion { get; init; }
    public CompatibilityLodStrategy? LodStrategy { get; init; }
    public long OldSize { get; init; }
    public long NewSize { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class CompatibilityFullRepairPlan
{
    public CompatibilityRepairPlan Metadata { get; init; } = new();
    public List<CompatibilityContentAction> Actions { get; } = [];
    public List<string> BlockingReasons { get; } = [];
    public List<string> Warnings { get; } = [];
    public int ActionCount => Metadata.ActionCount + Actions.Count;
    public bool CanRepair => ActionCount > 0 && BlockingReasons.Count == 0;
}

public sealed class CompatibilityBackupEntry
{
    public string BackupPath { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public bool CanRestore { get; init; }
    public bool CurrentMatches { get; init; }
    public string? Error { get; init; }
}

public enum CompatibilityBatchState { NoAction, Repairable, Blocked, Repaired, Failed }

public sealed class CompatibilityBatchItem
{
    public Guid ModId { get; init; }
    public string Name { get; init; } = string.Empty;
    public CompatibilityBatchState State { get; init; }
    public CompatibilityFullRepairPlan? Plan { get; init; }
    public CompatibilityRepairResult? Result { get; init; }
    public string? Error { get; init; }
}

public sealed class CompatibilityBatchProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public CompatibilityBatchItem Item { get; init; } = new();
}

/// <summary>伴随恢复只能使用主资源逐字节一致且伴随内容唯一的来源。</summary>
public sealed class GameCompanionRequest
{
    public ulong ResourceId { get; init; }
    public ulong TypeId { get; init; }
    public byte[] MainData { get; init; } = [];
    public bool Gpu { get; init; }
    public uint Size { get; init; }
}

public sealed class GameCompanionResult
{
    public byte[]? Payload { get; init; }
    public string? Error { get; init; }
}
