using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// A component within a logical mod
/// </summary>
public class LogicalModComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Component identifier (e.g., "hd2ir", "model", "texture")
    /// </summary>
    public string ComponentId { get; set; } = string.Empty;

    /// <summary>
    /// Component type
    /// </summary>
    public ComponentType Type { get; set; }

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Source package ID
    /// </summary>
    public Guid SourcePackageId { get; set; }

    /// <summary>
    /// Files that belong to this component
    /// </summary>
    public List<DeployableFile> DeployableFiles { get; set; } = new();

    /// <summary>
    /// Size of this component
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Whether this component is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Dependencies on other components
    /// </summary>
    public List<Guid> Dependencies { get; set; } = new();

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
