namespace HD2Mimnt.Core.Diagnostics;

public sealed record CrashDumpFile(string Path, DateTime ModifiedUtc, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record CrashModule(string Name, string Version, string BaseAddress, string EndAddress);
public sealed record CrashFrame(int Index, string Module, string ModuleOffset, string Function, string Trust);
public sealed record CrashDumpAnalysis(string Reason, string Address, string MainModule,
    IReadOnlyList<CrashFrame> Frames, IReadOnlyList<CrashModule> Modules, DateTime? CapturedUtc = null);

public enum CrashAssessment { Inconclusive, ModClues }
public enum CrashClueKind { ManagedModuleInStack, ManagedModuleLoaded, PossibleHookLoaded }
public sealed record CrashClue(CrashClueKind Kind, string Module, IReadOnlyList<string> ModNames, bool Ambiguous, bool ApproximateStack);
public sealed record CrashDeploymentContext(Guid SessionId, DateTime StartedUtc, IReadOnlyList<string> ModNames);

public sealed record CrashDiagnosisReport(string DumpPath, DateTime AnalyzedUtc, CrashDumpAnalysis Analysis,
    bool IsHelldiversDump, CrashAssessment Assessment, IReadOnlyList<CrashClue> Clues,
    CrashDeploymentContext? Deployment, bool DeploymentAfterDump, string? ContextError,
    CrashDeploymentComparison? Comparison = null);

// 独立于用于清理的 DeploymentRecords；清理旧部署不能删除诊断历史。
public sealed record CrashModSnapshot(Guid ModId, string Name, string Version, string Hash, DateTime ModifiedUtc,
    string FileMetadataHash, string OptionsJson, int Order);
public sealed record CrashDeploymentSnapshot(Guid SessionId, Guid ProfileId, string ProfileName, string GamePath,
    DateTime StartedUtc, DateTime? EndedUtc, string Status, bool KnownGood, IReadOnlyList<CrashModSnapshot> Mods,
    Guid? SourceSessionId = null)
{
    public string Display => $"{StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {ProfileName} · {Mods.Count}";
}
public enum CrashModChangeKind { Added, Updated, OptionsChanged, OrderChanged, Removed }
public sealed record CrashModChange(Guid ModId, string Name, IReadOnlyList<CrashModChangeKind> Kinds);
public sealed record CrashDeploymentComparison(CrashDeploymentSnapshot Deployment, CrashDeploymentSnapshot? Baseline,
    IReadOnlyList<CrashModChange> Changes);
