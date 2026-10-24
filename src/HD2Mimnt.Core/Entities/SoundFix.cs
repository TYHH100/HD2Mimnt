namespace HD2Mimnt.Core.Entities;

public enum SoundFixBankState { Current, Repairable, Skipped, Unknown, Error }

public sealed record SoundBankReference(byte[]? Data, string? Error = null);

public sealed class SoundFixBankReport
{
    public string PatchPath { get; init; } = "";
    public ulong ResourceId { get; init; }
    public string Name { get; init; } = "";
    public SoundFixBankState State { get; init; }
    public int MissingEntries { get; init; }
    public int MissingMedia { get; init; }
    public int CustomEntries { get; init; }
    public int CustomMedia { get; init; }
    public string? Error { get; init; }
}

public sealed class SoundFixModReport
{
    public Guid ModId { get; init; }
    public string Name { get; init; } = "";
    public string ModPath { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public List<SoundFixBankReport> Banks { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool CanRepair => Errors.Count == 0 && Banks.Any(b => b.State == SoundFixBankState.Repairable);
}

public sealed record SoundFixProgress(int Completed, int Total, string ModName);
public sealed record SoundFixResult(Guid ModId, bool Changed, string? BackupPath = null, string? Error = null);
