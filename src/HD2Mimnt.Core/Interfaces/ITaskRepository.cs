using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Task 仓储接口
/// </summary>
public interface ITaskRepository
{
    Task<ModTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<ModTask>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<ModTask>> GetRunningTasksAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<ModTask>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task AddAsync(ModTask task, CancellationToken cancellationToken = default);
    Task UpdateAsync(ModTask task, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
