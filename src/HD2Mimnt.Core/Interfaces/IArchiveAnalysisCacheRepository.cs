using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for managing archive analysis cache
/// </summary>
public interface IArchiveAnalysisCacheRepository
{
    /// <summary>
    /// Gets cached analysis for an archive
    /// </summary>
    Task<ArchiveAnalysisCache?> GetAsync(string filePath, long fileSize, DateTime lastModified, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves analysis cache entry
    /// </summary>
    Task SaveAsync(ArchiveAnalysisCache cache, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates last accessed time
    /// </summary>
    Task UpdateAccessAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes expired cache entries
    /// </summary>
    Task DeleteExpiredAsync(int expirationDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cache entries
    /// </summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
