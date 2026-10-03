using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Mod Repository 接口
/// </summary>
public interface IModRepository
{
    Task<Mod?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> SearchAsync(string keyword, CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> GetByTagAsync(string tag, CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> GetByCategoryAsync(ModCategory category, CancellationToken cancellationToken = default);
    Task<IEnumerable<Mod>> GetByStatusAsync(ModStatus status, CancellationToken cancellationToken = default);
    Task AddAsync(Mod mod, CancellationToken cancellationToken = default);
    Task UpdateAsync(Mod mod, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<long> GetTotalSizeAsync(CancellationToken cancellationToken = default);
    Task<int> GetCountAsync(CancellationToken cancellationToken = default);
}
