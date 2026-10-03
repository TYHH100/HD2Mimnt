using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Source package information
/// </summary>
public class PackageSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Package name (e.g., "AIRI.hd2irpack.zip")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Physical path to the package
    /// </summary>
    public string PhysicalPath { get; set; } = string.Empty;

    /// <summary>
    /// Archive type
    /// </summary>
    public ArchiveType ArchiveType { get; set; }

    /// <summary>
    /// Size in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Content hash
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Parent package ID (if this is nested)
    /// </summary>
    public Guid? ParentPackageId { get; set; }

    /// <summary>
    /// Depth in archive hierarchy
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Path within parent archive
    /// </summary>
    public string? PathInParent { get; set; }
}
