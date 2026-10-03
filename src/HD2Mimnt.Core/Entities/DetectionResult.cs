using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Result of component type detection
/// </summary>
public class DetectionResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Is this HD2IR content?
    /// </summary>
    public bool IsHd2Ir { get; set; }

    /// <summary>
    /// Detected component type
    /// </summary>
    public ComponentType ComponentType { get; set; }

    /// <summary>
    /// Detection confidence level
    /// </summary>
    public DetectionConfidence Confidence { get; set; }

    /// <summary>
    /// Reason for detection
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Files that contributed to detection
    /// </summary>
    public List<string> DetectedFiles { get; set; } = new();

    /// <summary>
    /// Detected directory structure patterns
    /// </summary>
    public List<string> DetectedStructure { get; set; } = new();

    /// <summary>
    /// Additional metadata from detection
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Timestamp of detection
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}
