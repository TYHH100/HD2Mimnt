using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for merging component candidates into logical mods
/// </summary>
public interface IModMerger
{
    /// <summary>
    /// Analyzes component candidates and determines merge strategy
    /// </summary>
    /// <param name="candidates">Component candidates to merge</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Merge result with logical mods</returns>
    Task<MergeResult> MergeAsync(List<ComponentCandidate> candidates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if components should be merged based on heuristics
    /// </summary>
    /// <param name="candidates">Component candidates to evaluate</param>
    /// <returns>True if components should be merged</returns>
    bool ShouldMerge(List<ComponentCandidate> candidates);

    /// <summary>
    /// Calculates merge confidence based on various factors
    /// </summary>
    /// <param name="candidates">Component candidates to evaluate</param>
    /// <returns>Confidence level for merging</returns>
    Enums.DetectionConfidence CalculateMergeConfidence(List<ComponentCandidate> candidates);
}
