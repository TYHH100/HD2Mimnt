namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Archive analysis cache entry to avoid re-scanning unchanged archives
/// </summary>
public class ArchiveAnalysisCache
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// File path
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Content hash (full or fast fingerprint)
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Cached archive tree (serialized)
    /// </summary>
    public string ArchiveTreeJson { get; set; } = string.Empty;

    /// <summary>
    /// Cached detection results (serialized)
    /// </summary>
    public string DetectionResultsJson { get; set; } = string.Empty;

    /// <summary>
    /// When this cache entry was created
    /// </summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time this cache entry was accessed
    /// </summary>
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of times this cache entry was used
    /// </summary>
    public int AccessCount { get; set; }
}
