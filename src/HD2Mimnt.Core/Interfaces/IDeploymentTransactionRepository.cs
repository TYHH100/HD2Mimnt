using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for deployment transaction tracking
/// </summary>
public interface IDeploymentTransactionRepository
{
    /// <summary>
    /// Adds a new transaction record
    /// </summary>
    Task AddAsync(DeploymentTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a transaction by ID
    /// </summary>
    Task<DeploymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a transaction by session ID
    /// </summary>
    Task<DeploymentTransaction?> GetBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all transactions for a profile
    /// </summary>
    Task<List<DeploymentTransaction>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all transactions for a logical mod
    /// </summary>
    Task<List<DeploymentTransaction>> GetByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all transactions for a traditional mod
    /// </summary>
    Task<List<DeploymentTransaction>> GetByModIdAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the latest transaction
    /// </summary>
    Task<DeploymentTransaction?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates transaction status
    /// </summary>
    Task UpdateStatusAsync(Guid id, Core.Enums.DeploymentStatus status, DateTime? endTime = null, string? errorMessage = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a transaction
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all transactions for a session
    /// </summary>
    Task DeleteBySessionIdAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
