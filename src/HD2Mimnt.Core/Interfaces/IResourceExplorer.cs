namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for exploring and managing Mod resources.
/// </summary>
public interface IResourceExplorer
{
    /// <summary>
    /// Gets the resource tree for a Mod.
    /// </summary>
    /// <param name="modId">Mod ID.</param>
    /// <param name="forceRefresh">Whether to force refresh the cached tree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Root node of the resource tree.</returns>
    Task<Entities.ResourceNode> GetResourceTreeAsync(string modId, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for resources matching a query within a Mod.
    /// </summary>
    /// <param name="modId">Mod ID.</param>
    /// <param name="query">Search query (searches file names and paths).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching resource nodes.</returns>
    Task<List<Entities.ResourceNode>> SearchResourcesAsync(string modId, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets dependency information for a specific resource.
    /// </summary>
    /// <param name="modId">Mod ID.</param>
    /// <param name="resourcePath">Relative path to the resource within the Mod.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resource dependency information.</returns>
    Task<Entities.ResourceDependency> GetDependenciesAsync(string modId, string resourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the integrity of a Mod's resources.
    /// Checks for missing files, broken references, and V1 file pairing issues.
    /// </summary>
    /// <param name="modId">Mod ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of error messages (empty if all valid).</returns>
    Task<List<string>> ValidateIntegrityAsync(string modId, CancellationToken cancellationToken = default);
}
