namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents an armor option (gameplay or appearance).
/// </summary>
public class ArmorOption
{
    /// <summary>
    /// Gets or sets the unique identifier for this armor option.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of files associated with this armor.
    /// </summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this armor option is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}
