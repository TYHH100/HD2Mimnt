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
    /// <summary>只回填判重指纹派生缓存，不覆盖同一行的其它字段。</summary>
    Task SetContentFingerprintAsync(Guid id, string fingerprint, CancellationToken cancellationToken = default);
    /// <summary>判重候选：内容身份（哈希或指纹）命中的行，加上尚未持久化身份、需要补算指纹的旧行。</summary>
    Task<IEnumerable<Mod>> FindContentCandidatesAsync(string hash, string contentFingerprint, CancellationToken cancellationToken = default);
    /// <summary>清单 GUID 命中的行；未持久化清单的旧行不在结果中。</summary>
    Task<IEnumerable<Mod>> FindByManifestGuidAsync(Guid manifestGuid, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<long> GetTotalSizeAsync(CancellationToken cancellationToken = default);
    Task<int> GetCountAsync(CancellationToken cancellationToken = default);
}
