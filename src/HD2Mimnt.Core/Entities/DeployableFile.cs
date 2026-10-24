namespace HD2Mimnt.Core.Entities;

/// <summary>
/// A file that can be deployed from a component
/// </summary>
public class DeployableFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Source path within the package
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Target deployment path (relative to game directory)
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Content hash
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Source package ID
    /// </summary>
    public Guid PackageId { get; set; }

    /// <summary>
    /// Is this a shared resource?
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
