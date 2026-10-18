namespace HD2Mimnt.Core.Entities;

/// <summary>资源 ID 与归档 ID 分属不同命名空间，不能互相回退匹配。</summary>
public sealed class CompatibilityResourceNames
{
    public IReadOnlyDictionary<ulong, string> Resources { get; init; } = new Dictionary<ulong, string>();
    public IReadOnlyDictionary<ulong, string> Archives { get; init; } = new Dictionary<ulong, string>();
    public DateTime? UpdatedAtUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? UpdateError { get; init; }
}
