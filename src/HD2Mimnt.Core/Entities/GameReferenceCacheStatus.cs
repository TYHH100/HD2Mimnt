namespace HD2Mimnt.Core.Entities;

public enum GameReferenceCacheState { Missing, Ready, Stale, Unavailable }

public sealed record GameReferenceCacheStatus
{
    public GameReferenceCacheState State { get; init; }
    public string CachePath { get; init; } = string.Empty;
    public DateTime? BuiltAtUtc { get; init; }
    public int PackageCount { get; init; }
    public int UnitCount { get; init; }
    public string? Error { get; init; }
}

public sealed record GameReferenceBuildProgress(int Completed, int Total);
