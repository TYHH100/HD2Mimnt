namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Progress of one HTTP range segment. <see cref="Downloaded"/> is the written offset relative
/// to <see cref="Start"/>; the segment spans [Start, End].
/// </summary>
public sealed record DownloadSegmentProgress(long Start, long End, long Downloaded);

/// <summary>
/// Represents download progress information.
/// </summary>
public class DownloadProgress
{
    /// <summary>
    /// Gets or sets the total bytes to download.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets the bytes downloaded so far.
    /// </summary>
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public double Percentage => TotalBytes > 0 ? (DownloadedBytes * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Gets or sets the download speed in bytes per second.
    /// </summary>
    public long BytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the current status message.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets per-segment progress for multi-connection downloads; null for
    /// unknown-length or single-connection transfers.
    /// </summary>
    public IReadOnlyList<DownloadSegmentProgress>? Segments { get; set; }
}
