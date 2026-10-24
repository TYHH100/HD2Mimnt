using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

public sealed partial class ModCompatibilityService
{
    public Task<GameReferenceCacheStatus> GetGameDataStatusAsync(CancellationToken cancellationToken = default)
        => Cache.GetCacheStatusAsync(GameDirectory, cancellationToken);

    public Task<GameReferenceCacheStatus> BuildGameDataAsync(bool force = false,
        IProgress<GameReferenceBuildProgress>? progress = null, CancellationToken cancellationToken = default)
        => Cache.BuildCacheAsync(GameDirectory, force, progress, cancellationToken);

    /// <summary>只自动更新已有的过期索引；首次全量构建由用户在设置页发起。检查时仍可按需构建。</summary>
    public async Task<GameReferenceCacheStatus> RefreshGameDataIfStaleAsync(
        IProgress<GameReferenceBuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var status = await GetGameDataStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.State == GameReferenceCacheState.Stale
            ? await BuildGameDataAsync(progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false) : status;
    }

    private string GameDirectory => _settings?.GetEffectiveGamePath() ?? string.Empty;
    private IGameReferenceCache Cache => _gameReferences as IGameReferenceCache
        ?? throw new NotSupportedException("游戏参考数据缓存服务未配置。");
}
