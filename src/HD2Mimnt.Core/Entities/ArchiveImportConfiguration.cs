namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Configuration for archive inspection and import
/// </summary>
public class ArchiveImportConfiguration
{
    /// <summary>
    /// Maximum depth for nested archive inspection (default: 5)
    /// </summary>
    public int MaxArchiveInspectionDepth { get; set; } = 5;

    /// <summary>
    /// Whether to use fast fingerprinting or full hash
    /// </summary>
    public bool UseFastFingerprint { get; set; } = true;

    /// <summary>
    /// Maximum file size for automatic import (in bytes, 0 = unlimited)
    /// </summary>
    public long MaxAutoImportSize { get; set; } = 0;

    /// <summary>
    /// Whether to cache analysis results
    /// </summary>
    public bool EnableAnalysisCache { get; set; } = true;

    /// <summary>
    /// Cache expiration time in days
    /// </summary>
    public int CacheExpirationDays { get; set; } = 30;

    /// <summary>
    /// Whether to automatically merge high-confidence components
    /// </summary>
    public bool AutoMergeHighConfidence { get; set; } = true;

    /// <summary>
    /// Whether to prompt for low-confidence merges
    /// </summary>
    public bool PromptForLowConfidenceMerge { get; set; } = true;

    /// <summary>
    /// Temporary extraction directory
    /// </summary>
    public string TempExtractionPath { get; set; } = Path.Combine(Path.GetTempPath(), "HD2Mimnt", "ArchiveTemp");
}
