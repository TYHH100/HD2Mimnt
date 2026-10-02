using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository interface for AI provider configuration
/// </summary>
public interface IAIProviderConfigRepository
{
    /// <summary>
    /// Get the active AI provider configuration
    /// </summary>
    Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all AI provider configurations
    /// </summary>
    Task<List<AIProviderConfig>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get AI provider configuration by ID
    /// </summary>
    Task<AIProviderConfig?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if provider exists
    /// </summary>
    Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add new AI provider configuration
    /// </summary>
    Task AddAsync(AIProviderConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update existing AI provider configuration
    /// </summary>
    Task UpdateAsync(AIProviderConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete AI provider configuration
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set a provider as active (disables all others)
    /// </summary>
    Task SetActiveProviderAsync(string id, CancellationToken cancellationToken = default);
}
