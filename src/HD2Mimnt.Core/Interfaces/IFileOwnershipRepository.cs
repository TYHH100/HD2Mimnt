using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for managing file ownership tracking
/// </summary>
public interface IFileOwnershipRepository
{
    /// <summary>
    /// Adds file ownership record
    /// </summary>
    Task AddAsync(FileOwnership ownership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets ownership for a specific deployed file
    /// </summary>
    Task<FileOwnership?> GetByDeployPathAsync(string deployPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files owned by a logical mod
    /// </summary>
    Task<List<FileOwnership>> GetByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files owned by a component
    /// </summary>
    Task<List<FileOwnership>> GetByComponentIdAsync(Guid componentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes ownership records for a logical mod
    /// </summary>
    Task DeleteByLogicalModIdAsync(Guid logicalModId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates reference count for shared resources
    /// </summary>
    Task UpdateReferenceCountAsync(string deployPath, int delta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all shared resources
    /// </summary>
    Task<List<FileOwnership>> GetSharedResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all file ownership records
    /// </summary>
    Task<List<FileOwnership>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes ownership record by deploy path
    /// </summary>
    Task DeleteByDeployPathAsync(string deployPath, CancellationToken cancellationToken = default);
}
