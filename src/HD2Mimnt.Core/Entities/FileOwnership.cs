using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// File ownership tracking for deployment
/// </summary>
public class FileOwnership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Logical mod ID that owns this file
    /// </summary>
    public Guid LogicalModId { get; set; }

    /// <summary>
    /// Component ID within the logical mod
    /// </summary>
    public Guid ComponentId { get; set; }

    /// <summary>
    /// Package ID that provided the file
    /// </summary>
    public Guid PackageId { get; set; }

    /// <summary>
    /// Deployed file path (absolute or relative to game directory)
    /// </summary>
    public string DeployPath { get; set; } = string.Empty;

    /// <summary>
    /// Content hash at deployment time
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Transaction ID that deployed this file
    /// </summary>
    public Guid TransactionId { get; set; }

    /// <summary>
    /// When the file was deployed
    /// </summary>
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Is this a shared resource?
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// Reference count (for shared resources)
    /// </summary>
    public int ReferenceCount { get; set; } = 1;
}
