namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents the complete tree structure of an archive and its nested contents
/// </summary>
public class ArchiveTree
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Root node of the archive tree
    /// </summary>
    public ArchiveNode Root { get; set; } = null!;

    /// <summary>
    /// Maximum depth found in the tree
    /// </summary>
    public int MaxDepth { get; set; }

    /// <summary>
    /// Total number of nodes in the tree
    /// </summary>
    public int TotalNodes { get; set; }

    /// <summary>
    /// Total number of archives found (including nested)
    /// </summary>
    public int TotalArchives { get; set; }

    /// <summary>
    /// Total size of all content
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// When the tree was built
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// All detected components in the tree
    /// </summary>
    public List<ComponentCandidate> ComponentCandidates { get; set; } = new();

    /// <summary>
    /// Whether the maximum depth limit was exceeded
    /// </summary>
    public bool DepthLimitExceeded { get; set; }

    /// <summary>
    /// Warning messages during tree construction
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
