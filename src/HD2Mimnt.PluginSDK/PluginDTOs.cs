namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Safe DTO representing mod information (no internal implementation details)
/// </summary>
public class PluginModInfo
{
    /// <summary>
    /// Unique mod identifier
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Mod name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Mod description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Mod version
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Mod author
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// Mod category
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Mod tags
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Mod source path
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// File count in the mod
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Total size in bytes
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Import date
    /// </summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>
    /// Custom metadata (key-value pairs)
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Safe DTO representing profile information
/// </summary>
public class PluginProfileInfo
{
    /// <summary>
    /// Unique profile identifier
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Profile name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Profile description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this profile is currently active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Number of mods in this profile
    /// </summary>
    public int ModCount { get; set; }

    /// <summary>
    /// Creation date
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Context for deployment operations
/// </summary>
public class PluginDeploymentContext
{
    /// <summary>
    /// Profile being deployed
    /// </summary>
    public PluginProfileInfo Profile { get; set; } = new();

    /// <summary>
    /// Mods being deployed
    /// </summary>
    public List<PluginModInfo> Mods { get; set; } = new();

    /// <summary>
    /// Deployment method (Copy, Symlink, Hardlink)
    /// </summary>
    public string DeploymentMethod { get; set; } = string.Empty;

    /// <summary>
    /// Target directory
    /// </summary>
    public string TargetDirectory { get; set; } = string.Empty;
}
