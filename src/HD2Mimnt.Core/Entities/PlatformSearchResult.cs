namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents the result of a platform search operation.
/// </summary>
public class PlatformSearchResult
{
    /// <summary>Describes a limited search source, or empty for an unrestricted search.</summary>
    public string ScopeDescription { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of Mod information from the search.
    /// </summary>
    public List<PlatformModInfo> Mods { get; set; } = new();

    /// <summary>
    /// Gets or sets the total count of results across all pages.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the current page number (1-based).
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Gets or sets the page size.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Gets whether there are more pages available.
    /// </summary>
    public bool HasNextPage => Page * PageSize < TotalCount;
}
