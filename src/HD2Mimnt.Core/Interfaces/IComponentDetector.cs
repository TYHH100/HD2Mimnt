using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for detecting component types in archives
/// </summary>
public interface IComponentDetector
{
    /// <summary>
    /// Detects component type for an archive node
    /// </summary>
    /// <param name="node">Archive node to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result</returns>
    Task<DetectionResult> DetectComponentAsync(ArchiveNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts component candidates from an archive tree
    /// </summary>
    /// <param name="tree">Archive tree to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of component candidates</returns>
    Task<List<ComponentCandidate>> ExtractCandidatesAsync(ArchiveTree tree, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detection patterns for a specific component type
    /// </summary>
    /// <param name="componentType">Component type</param>
    /// <returns>List of patterns for the component type</returns>
    IEnumerable<string> GetPatternsForType(ComponentType componentType);
}
