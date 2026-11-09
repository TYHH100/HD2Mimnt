namespace HD2Mimnt.Core.Entities;

/// <summary>Options for one HTTP download. Parallelism is bounded per file.</summary>
public sealed class DownloadOptions
{
    public const int MaxAllowedParallelSegments = 256;
    public const int ParallelSegmentsWarningThreshold = 64;

    public int MaxParallelSegments { get; init; } = 4;
    public long MinimumSegmentSize { get; init; } = 4 * 1024 * 1024;
    /// <summary>Additional attempts for transient failures of a range request.</summary>
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Keep validated partial downloads on interruption and reuse them on the next attempt.</summary>
    public bool EnableResume { get; init; } = true;
    /// <summary>Allow publication to replace an existing destination.</summary>
    public bool OverwriteExisting { get; init; } = true;
    /// <summary>
    /// 期望内容哈希，格式 "sha256:&lt;hex&gt;" 或 "md5:&lt;hex&gt;"（与 GitHub release digest 一致）。
    /// 设置后下载完成会做完整性校验，不匹配即删除文件并抛出 InvalidDataException。
    /// </summary>
    public string? ExpectedHash { get; init; }
    /// <summary>
    /// 允许明文 http:// 下载。默认拒绝；仅用户显式选择的场景可开启。
    /// 用户自配置代理的直连下载（DirectDownloadService）不走本通道。
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParallelSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxParallelSegments, MaxAllowedParallelSegments);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumSegmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetries, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRetries, 10);
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (ExpectedHash is { Length: > 0 } hash &&
            !System.Text.RegularExpressions.Regex.IsMatch(hash,
                "^(sha256:[0-9a-fA-F]{64}|md5:[0-9a-fA-F]{32})$"))
        {
            throw new ArgumentException("ExpectedHash 格式必须为 \"sha256:<64位十六进制>\" 或 \"md5:<32位十六进制>\"。", nameof(ExpectedHash));
        }
    }
}
