using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Result of merging component candidates into logical mods
/// </summary>
public class MergeResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The logical mods created from merging
    /// </summary>
    public List<LogicalMod> LogicalMods { get; set; } = new();

    /// <summary>
    /// Merge decision made
    /// </summary>
    public MergeDecision Decision { get; set; }

    /// <summary>
    /// Confidence in the merge
    /// </summary>
    public DetectionConfidence Confidence { get; set; }

    /// <summary>
    /// Reason for the merge decision
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Whether user confirmation was required
    /// </summary>
    public bool RequiredConfirmation { get; set; }

    /// <summary>
    /// Components that were merged
    /// </summary>
    public List<ComponentCandidate> MergedComponents { get; set; } = new();

    /// <summary>
    /// Warnings or issues during merge
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// When the merge was performed
    /// </summary>
    public DateTime MergedAt { get; set; } = DateTime.UtcNow;
}
