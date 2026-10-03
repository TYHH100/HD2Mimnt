using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a node in the archive tree (can be an archive or a file/directory within)
/// </summary>
public class ArchiveNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the archive or entry
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Archive type (if this node is an archive)
    /// </summary>
    public ArchiveType ArchiveType { get; set; }

    /// <summary>
    /// Parent node (null if root)
    /// </summary>
    public ArchiveNode? Parent { get; set; }

    /// <summary>
    /// Child nodes (nested archives or entries)
    /// </summary>
    public List<ArchiveNode> Children { get; set; } = new();

    /// <summary>
    /// Depth in the archive tree (0 = root)
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Size in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Number of entries (for archives)
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>
    /// Full path within the archive hierarchy
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Physical path on disk (for root archives)
    /// </summary>
    public string? PhysicalPath { get; set; }

    /// <summary>
    /// Is this node an archive?
    /// </summary>
    public bool IsArchive { get; set; }

    /// <summary>
    /// Is this node a directory?
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Detection results for this node
    /// </summary>
    public DetectionResult? DetectionResult { get; set; }

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// Content hash (if calculated)
    /// </summary>
    public string? Hash { get; set; }
}
