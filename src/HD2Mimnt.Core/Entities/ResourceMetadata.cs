namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents metadata for a resource file.
/// </summary>
public class ResourceMetadata
{
    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SHA256 hash of the file.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file type description (e.g., "JSON Configuration", "V1 Patch File").
    /// </summary>
    public string FileType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the creation time.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets the last modified time.
    /// </summary>
    public DateTime Modified { get; set; }

    /// <summary>
    /// Gets or sets additional properties (key-value pairs).
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of resource references found in this file.
    /// </summary>
    public List<ResourceReference> References { get; set; } = new();

    /// <summary>
    /// Gets whether this file is a V1-specific file (.patch_0, .gpu_resources, .stream).
    /// </summary>
    public bool IsV1File { get; set; }
}
