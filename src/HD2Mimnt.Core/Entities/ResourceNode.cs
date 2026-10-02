using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a node in the resource tree (file or directory).
/// </summary>
public class ResourceNode
{
    /// <summary>
    /// Gets or sets the name of the resource (file or directory name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path of the resource.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the relative path within the Mod directory.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of the resource node.
    /// </summary>
    public ResourceNodeType Type { get; set; }

    /// <summary>
    /// Gets or sets the size of the resource in bytes (0 for directories).
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last modified time of the resource.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Gets or sets the file extension (e.g., ".json", ".patch_0"). Empty for directories.
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the child nodes (for directories).
    /// </summary>
    public List<ResourceNode> Children { get; set; } = new();

    /// <summary>
    /// Gets or sets the metadata associated with this resource (optional).
    /// </summary>
    public ResourceMetadata? Metadata { get; set; }

    /// <summary>
    /// Gets whether this node is a directory.
    /// </summary>
    public bool IsDirectory => Type == ResourceNodeType.Directory;

    /// <summary>
    /// Gets whether this node is a file.
    /// </summary>
    public bool IsFile => Type == ResourceNodeType.File;
}
