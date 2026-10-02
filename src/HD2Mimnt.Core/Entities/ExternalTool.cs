namespace HD2Mimnt.Core.Entities;

/// <summary>
/// External tool registration entity
/// </summary>
public class ExternalTool
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Tool display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tool description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Executable path
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Command line arguments template
    /// Supports placeholders: {FilePath}, {FileName}, {DirectoryPath}
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Working directory (optional, defaults to file directory)
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Icon path or name
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// File extensions this tool can handle (comma-separated, e.g., ".txt,.log")
    /// </summary>
    public string? SupportedExtensions { get; set; }

    /// <summary>
    /// Whether this tool is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Display order in menus
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime? ModifiedAt { get; set; }
}
