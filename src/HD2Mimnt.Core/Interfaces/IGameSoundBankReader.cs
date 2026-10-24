using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IGameSoundBankReader
{
    Task<IReadOnlyDictionary<ulong, SoundBankReference>> ReadSoundBanksAsync(string gameDirectory,
        IReadOnlyCollection<ulong> resourceIds, CancellationToken cancellationToken = default);
}
