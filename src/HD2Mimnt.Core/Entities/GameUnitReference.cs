namespace HD2Mimnt.Core.Entities;

public enum GameUnitReferenceStatus { Found, Missing, Ambiguous, Error }

/// <summary>仅用于 Unit 版本比较；不表示布局、材质或 GPU 数据相同。</summary>
public sealed class GameUnitReference
{
    public ulong ResourceId { get; init; }
    public GameUnitReferenceStatus Status { get; init; }
    public uint? Version { get; init; }
    public IReadOnlyList<string> Packages { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public byte[]? UnitData { get; init; }
    public byte[] LodGroupData { get; init; } = [];
    public uint[] MeshIds { get; init; } = [];
    public uint GpuSize { get; init; }
}

public sealed class GameUnitReferenceReport
{
    public string Source { get; init; } = string.Empty;
    public bool IndexComplete { get; set; }
    public bool IndexCacheHit { get; set; }
    public Dictionary<ulong, GameUnitReference> Units { get; init; } = [];
    public List<string> Issues { get; init; } = [];
}
