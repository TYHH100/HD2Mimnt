using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a logical mod composed of multiple components
/// </summary>
public class LogicalMod
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User-facing mod name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Version string
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Author
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Components that make up this logical mod
    /// </summary>
    public List<LogicalModComponent> Components { get; set; } = new();

    /// <summary>
    /// Source packages
    /// </summary>
    public List<PackageSource> Packages { get; set; } = new();

    /// <summary>
    /// Manifest version
    /// </summary>
    public LogicalModVersion ManifestVersion { get; set; } = LogicalModVersion.V1;

    /// <summary>
    /// When this logical mod was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this logical mod was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total size of all components
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Thumbnail path
    /// </summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>
    /// Tags
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
