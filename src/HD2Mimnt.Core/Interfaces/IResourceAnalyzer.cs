namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for analyzing Mod resources and extracting metadata.
/// </summary>
public interface IResourceAnalyzer
{
    /// <summary>
    /// Analyzes a Mod directory and generates a resource tree.
    /// </summary>
    /// <param name="modPath">Path to the Mod directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Root node of the resource tree.</returns>
    Task<Entities.ResourceNode> AnalyzeAsync(string modPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata for a specific file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resource metadata.</returns>
    Task<Entities.ResourceMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates the SHA256 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SHA256 hash as a hexadecimal string.</returns>
    Task<string> CalculateHashAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects references in a file (e.g., file paths in JSON).
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of resource references.</returns>
    Task<List<Entities.ResourceReference>> DetectReferencesAsync(string filePath, CancellationToken cancellationToken = default);
}
