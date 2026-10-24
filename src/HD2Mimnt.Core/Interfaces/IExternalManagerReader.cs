using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>只读读取外部管理器；不启动软件、不写入源数据库或源模组。</summary>
public interface IExternalManagerReader
{
    ExternalManagerKind Manager { get; }
    Task<ExternalMigrationPlan> ReadAsync(ExternalMigrationSource source, CancellationToken cancellationToken = default);
}
