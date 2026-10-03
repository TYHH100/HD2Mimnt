using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for managing logical mods
/// </summary>
public interface ILogicalModRepository
{
    /// <summary>
    /// Adds a logical mod to the repository
    /// </summary>
    Task<LogicalMod> AddAsync(LogicalMod logicalMod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing logical mod
    /// </summary>
    Task UpdateAsync(LogicalMod logicalMod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a logical mod by ID
    /// </summary>
    Task<LogicalMod?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all logical mods
    /// </summary>
    Task<List<LogicalMod>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a logical mod
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches logical mods by name
    /// </summary>
    Task<List<LogicalMod>> SearchByNameAsync(string name, CancellationToken cancellationToken = default);
}
