using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a reference from one resource file to another.
/// </summary>
public class ResourceReference
{
    /// <summary>
    /// Gets or sets the source file that contains the reference.
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target file being referenced.
    /// </summary>
    public string TargetFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of reference.
    /// </summary>
    public ReferenceType Type { get; set; }

    /// <summary>
    /// Gets or sets the context where the reference was found (e.g., JSON field name, line number).
    /// </summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// Gets whether the target file exists.
    /// </summary>
    public bool TargetExists { get; set; }
}
