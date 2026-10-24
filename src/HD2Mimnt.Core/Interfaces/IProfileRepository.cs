using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Profile 仓储接口
/// </summary>
public interface IProfileRepository
{
    Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Profile profile, CancellationToken cancellationToken = default);
    Task UpdateAsync(Profile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Profile> DuplicateAsync(Guid id, string newName, CancellationToken cancellationToken = default);
    Task SetActiveProfileAsync(Guid id, CancellationToken cancellationToken = default);
}
