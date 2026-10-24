using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Core.Interfaces;

public interface IExternalManagerWriter
{
    ExternalManagerKind Manager { get; }
    Task InspectAsync(ExternalMigrationPlan plan, CancellationToken cancellationToken = default);
    Task<ExternalMigrationResult> WriteAsync(ExternalMigrationPlan plan,
        IProgress<ExternalMigrationProgress>? progress = null, CancellationToken cancellationToken = default);
}
