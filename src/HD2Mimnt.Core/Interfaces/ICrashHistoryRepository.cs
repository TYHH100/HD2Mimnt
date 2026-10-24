using HD2Mimnt.Core.Diagnostics;

namespace HD2Mimnt.Core.Interfaces;

public interface ICrashHistoryRepository
{
    Task SaveAsync(CrashDeploymentSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CrashDeploymentSnapshot>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task MarkKnownGoodAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
