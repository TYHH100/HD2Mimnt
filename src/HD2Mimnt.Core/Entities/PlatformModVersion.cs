namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a specific version of a Mod from a platform.
/// </summary>
public class PlatformModVersion
{
    /// <summary>
    /// Gets or sets the version string.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this version was released.
    /// </summary>
    public DateTime ReleasedAt { get; set; }

    /// <summary>
    /// Gets or sets the download URL for this version.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Gets or sets the changelog for this version.
    /// </summary>
    public string ChangeLog { get; set; } = string.Empty;
}
