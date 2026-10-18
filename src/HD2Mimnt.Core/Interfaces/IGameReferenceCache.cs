using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IGameReferenceCache
{
    Task<GameReferenceCacheStatus> GetCacheStatusAsync(string gameDirectory, CancellationToken cancellationToken = default);
    Task<GameReferenceCacheStatus> BuildCacheAsync(string gameDirectory, bool force = false,
        IProgress<GameReferenceBuildProgress>? progress = null, CancellationToken cancellationToken = default);
}
