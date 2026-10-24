using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a search query for a Mod platform.
/// </summary>
public class PlatformSearchQuery
{
    /// <summary>
    /// Gets or sets the search query text.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of results per page.
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Gets or sets the page number (1-based).
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Gets or sets the sort order.
    /// </summary>
    public PlatformSearchSort SortBy { get; set; } = PlatformSearchSort.Relevance;

    /// <summary>
    /// Gets or sets additional platform-specific filters.
    /// </summary>
    public Dictionary<string, string> Filters { get; set; } = new();
}
