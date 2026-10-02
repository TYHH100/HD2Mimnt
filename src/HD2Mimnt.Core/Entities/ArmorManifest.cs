namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents an Integrated Armor Mod manifest.
/// </summary>
public class ArmorManifest
{
    /// <summary>
    /// Gets or sets the armor name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the armor description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the author name.
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version string.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of gameplay armor options.
    /// </summary>
    public List<ArmorOption> GameplayArmors { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of appearance armor options.
    /// </summary>
    public List<ArmorOption> AppearanceArmors { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of body variant options.
    /// </summary>
    public List<VariantOption> BodyVariants { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of head variant options.
    /// </summary>
    public List<VariantOption> HeadVariants { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this armor has a custom skeleton.
    /// </summary>
    public bool HasCustomSkeleton { get; set; }

    /// <summary>
    /// Gets or sets whether this armor has physics simulation.
    /// </summary>
    public bool HasPhysics { get; set; }

    /// <summary>
    /// Gets or sets whether this armor has illusion support.
    /// </summary>
    public bool HasIllusion { get; set; }

    /// <summary>
    /// Gets or sets whether this armor has anti-dismemberment.
    /// </summary>
    public bool HasAntiDismemberment { get; set; }

    /// <summary>
    /// Gets or sets whether this armor has Lua-ReShade support.
    /// </summary>
    public bool HasLuaReShade { get; set; }

    /// <summary>
    /// Gets or sets the list of required files/directories.
    /// </summary>
    public List<string> RequiredFiles { get; set; } = new();

    /// <summary>
    /// Gets or sets additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
