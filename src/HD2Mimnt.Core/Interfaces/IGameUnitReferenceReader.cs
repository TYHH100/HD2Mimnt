using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IGameUnitReferenceReader
{
    /// <param name="gameDirectory">游戏安装根目录。</param>
    Task<GameUnitReferenceReport> ReadAsync(string gameDirectory, IReadOnlyCollection<ulong> resourceIds,
        CancellationToken cancellationToken = default);
    Task<GameUnitReferenceReport> ReadDetailedAsync(string gameDirectory, IReadOnlyCollection<ulong> resourceIds,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameCompanionResult>> ReadCompanionsAsync(string gameDirectory,
        IReadOnlyList<GameCompanionRequest> requests, CancellationToken cancellationToken = default);
}
