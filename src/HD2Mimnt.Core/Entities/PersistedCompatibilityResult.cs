namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 兼容性检查结果摘要的持久化记录，用于跨会话回填角标。
/// 只存判定所需的摘要，不存逐补丁/逐 Unit 详情；ContentStamp 为检查时的模组目录内容戳，变化即失效。
/// </summary>
public sealed record PersistedCompatibilityResult
{
    public required Guid ModId { get; init; }
    public ModCompatibilityStatus Status { get; init; }
    public ModCompatibilityReason Reason { get; init; }
    public int UnitCount { get; init; }
    public int ComparedUnitCount { get; init; }
    public bool HasBlockingIssues { get; init; }
    public string ReferenceSource { get; init; } = string.Empty;
    public IReadOnlyList<string> Issues { get; init; } = [];
    public DateTime CheckedAtUtc { get; init; }
    public string ContentStamp { get; init; } = string.Empty;
}
