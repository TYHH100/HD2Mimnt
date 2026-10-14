namespace HD2Mimnt.Core.Entities;

/// <summary>Options for one HTTP download. Parallelism is bounded per file.</summary>
public sealed class DownloadOptions
{
    public int MaxParallelSegments { get; init; } = 4;
    public long MinimumSegmentSize { get; init; } = 4 * 1024 * 1024;
    /// <summary>Additional attempts for transient failures of a range request.</summary>
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Keep validated partial downloads on interruption and reuse them on the next attempt.</summary>
    public bool EnableResume { get; init; } = true;
    /// <summary>Allow publication to replace an existing destination.</summary>
    public bool OverwriteExisting { get; init; } = true;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParallelSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxParallelSegments, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumSegmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetries, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRetries, 10);
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
    }
}
