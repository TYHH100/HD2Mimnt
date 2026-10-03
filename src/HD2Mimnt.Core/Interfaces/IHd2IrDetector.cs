using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for detecting HD2IR content in archives
/// </summary>
public interface IHd2IrDetector
{
    /// <summary>
    /// Detects if an archive node contains HD2IR content
    /// </summary>
    /// <param name="node">Archive node to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result</returns>
    Task<DetectionResult> DetectAsync(ArchiveNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects HD2IR content from a list of file paths
    /// </summary>
    /// <param name="filePaths">File paths to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result</returns>
    Task<DetectionResult> DetectFromPathsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets HD2IR-specific file patterns
    /// </summary>
    /// <returns>List of file patterns that indicate HD2IR content</returns>
    IEnumerable<string> GetHd2IrPatterns();
}
