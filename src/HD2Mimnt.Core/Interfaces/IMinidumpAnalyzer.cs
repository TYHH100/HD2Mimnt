using HD2Mimnt.Core.Diagnostics;

namespace HD2Mimnt.Core.Interfaces;

public interface IMinidumpAnalyzer
{
    bool IsAvailable { get; }
    Task<CrashDumpAnalysis> AnalyzeAsync(string dumpPath, CancellationToken cancellationToken = default);
}
