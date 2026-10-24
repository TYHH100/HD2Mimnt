namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository interface for external tool management
/// </summary>
public interface IExternalToolRepository
{
    /// <summary>
    /// Get all external tools
    /// </summary>
    Task<List<Entities.ExternalTool>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get enabled external tools
    /// </summary>
    Task<List<Entities.ExternalTool>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get external tool by ID
    /// </summary>
    Task<Entities.ExternalTool?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get tools that support a specific file extension
    /// </summary>
    Task<List<Entities.ExternalTool>> GetByExtensionAsync(string extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a tool with the given ID exists
    /// </summary>
    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a new external tool
    /// </summary>
    Task AddAsync(Entities.ExternalTool tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing external tool
    /// </summary>
    Task UpdateAsync(Entities.ExternalTool tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an external tool
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
