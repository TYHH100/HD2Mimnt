namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a body or head variant option.
/// </summary>
public class VariantOption
{
    /// <summary>
    /// Gets or sets the variant ID (numeric identifier).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of files associated with this variant.
    /// </summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this variant is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}
