namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents information about a Mod from a platform.
/// </summary>
public class PlatformModInfo
{
    /// <summary>
    /// Gets or sets the platform-specific Mod ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Mod name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Mod description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the author name.
    /// </summary>
    public string Author { get; set; } = string.Empty;
    public string? AuthorUrl { get; set; }

    /// <summary>
    /// Gets or sets the version string.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the Mod was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the total download count.
    /// </summary>
    public long DownloadCount { get; set; }

    /// <summary>
    /// Gets or sets the thumbnail URL.
    /// </summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the download URL.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of tags.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Gets or sets additional platform-specific metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
