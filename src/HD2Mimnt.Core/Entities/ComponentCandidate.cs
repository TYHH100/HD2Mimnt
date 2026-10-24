using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// A candidate component detected in an archive
/// </summary>
public class ComponentCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Component type
    /// </summary>
    public ComponentType ComponentType { get; set; }

    /// <summary>
    /// Component name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Archive node that contains this component
    /// </summary>
    public ArchiveNode SourceNode { get; set; } = null!;

    /// <summary>
    /// Detection result
    /// </summary>
    public DetectionResult DetectionResult { get; set; } = null!;

    /// <summary>
    /// Related files for this component
    /// </summary>
    public List<string> RelatedFiles { get; set; } = new();

    /// <summary>
    /// Size of the component
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
