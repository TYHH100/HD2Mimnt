namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents dependency relationships for a resource.
/// </summary>
public class ResourceDependency
{
    /// <summary>
    /// Gets or sets the Mod ID.
    /// </summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resource path.
    /// </summary>
    public string ResourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of files this resource depends on.
    /// </summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of files that depend on this resource.
    /// </summary>
    public List<string> RequiredBy { get; set; } = new();

    /// <summary>
    /// Gets whether this resource has any dependencies.
    /// </summary>
    public bool HasDependencies => DependsOn.Count > 0;

    /// <summary>
    /// Gets whether this resource is required by other resources.
    /// </summary>
    public bool IsRequired => RequiredBy.Count > 0;
}
