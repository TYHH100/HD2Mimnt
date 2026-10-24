using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>Downloads HTTP/HTTPS files without buffering the entire file in memory.</summary>
public interface IHttpDownloadService
{
    /// <summary>Returns a seekable stream at position zero. Dispose it to delete the completed file.
    /// Interrupted transfers retain validated checkpoints when resume is enabled.
    /// A stable resumeKey can identify a file whose signed URL changes between attempts.</summary>
    Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        string? resumeKey = null);

    /// <summary>Publishes the complete file only after a successful download; preserves an existing target on failure.</summary>
    Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        string? resumeKey = null);
}
