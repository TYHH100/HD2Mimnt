using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for managing Profile-LogicalMod associations
/// </summary>
public interface IProfileLogicalModRepository
{
    /// <summary>
    /// Adds a LogicalMod to a Profile
    /// </summary>
    Task AddAsync(ProfileLogicalMod profileLogicalMod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all LogicalMods for a specific Profile
    /// </summary>
    Task<List<ProfileLogicalMod>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets enabled LogicalMods for a Profile, ordered by Order
    /// </summary>
    Task<List<ProfileLogicalMod>> GetEnabledByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a ProfileLogicalMod's enabled state
    /// </summary>
    Task UpdateEnabledAsync(Guid profileId, Guid logicalModId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a ProfileLogicalMod's order
    /// </summary>
    Task UpdateOrderAsync(Guid profileId, Guid logicalModId, int order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates enabled component IDs for a ProfileLogicalMod
    /// </summary>
    Task UpdateEnabledComponentsAsync(Guid profileId, Guid logicalModId, List<Guid> enabledComponentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a LogicalMod from a Profile
    /// </summary>
    Task RemoveAsync(Guid profileId, Guid logicalModId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all LogicalMods from a Profile
    /// </summary>
    Task RemoveAllByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a LogicalMod is in a Profile
    /// </summary>
    Task<bool> ExistsAsync(Guid profileId, Guid logicalModId, CancellationToken cancellationToken = default);
}
