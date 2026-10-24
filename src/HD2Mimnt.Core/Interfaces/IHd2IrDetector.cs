using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for detecting HD2IAS content in archives
/// </summary>
public interface IHd2IrDetector
{
    /// <summary>
    /// Detects if an archive node contains HD2IAS content
    /// </summary>
    /// <param name="node">Archive node to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result</returns>
    Task<DetectionResult> DetectAsync(ArchiveNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects HD2IAS content from a list of file paths
    /// </summary>
    /// <param name="filePaths">File paths to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result</returns>
    Task<DetectionResult> DetectFromPathsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets HD2IAS-specific file patterns
    /// </summary>
    /// <returns>List of file patterns that indicate HD2IAS content</returns>
    IEnumerable<string> GetHd2IrPatterns();
}
