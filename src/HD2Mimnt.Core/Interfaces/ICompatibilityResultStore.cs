using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>兼容性检查摘要的持久化存储。实现必须线程安全，可从后台线程并发调用。</summary>
public interface ICompatibilityResultStore
{
    /// <summary>按 ModId 整行覆盖写入。</summary>
    Task UpsertAsync(IReadOnlyCollection<PersistedCompatibilityResult> results, CancellationToken cancellationToken = default);

    /// <summary>读取全部摘要；实现应顺带清理模组已删除的孤儿行。</summary>
    Task<IReadOnlyList<PersistedCompatibilityResult>> GetAllAsync(CancellationToken cancellationToken = default);
}
