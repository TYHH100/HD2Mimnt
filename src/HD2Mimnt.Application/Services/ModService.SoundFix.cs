using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.SoundFix;

namespace HD2Mimnt.Application.Services;

public partial class ModService
{
    internal async Task<SoundFixResult> MutateSoundFixAsync(Guid id,
        Func<Mod, Task<SoundFixTransaction>> prepare, CancellationToken token)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, token) ?? throw new KeyNotFoundException("模组已不存在。");
            using var transaction = await prepare(mod);
            if (!transaction.HasChanges) return new(id, false);
            var oldHash = mod.Hash;
            var oldSize = mod.Size;
            var oldModified = mod.ModifiedAt;
            var oldTypes = mod.ResourceTypeIds;
            try
            {
                await transaction.ApplyAsync(token);
                mod.Hash = "";
                mod.ResourceTypeIds = null;
                mod.Size = Math.Max(0, mod.Size + transaction.SizeDelta);
                mod.ModifiedAt = DateTime.UtcNow;
                await _modRepository.UpdateAsync(mod, CancellationToken.None);
                transaction.Complete();
            }
            catch (Exception error)
            {
                mod.Hash = oldHash; mod.Size = oldSize; mod.ModifiedAt = oldModified; mod.ResourceTypeIds = oldTypes;
                try { await transaction.RollbackAsync(); }
                catch (Exception rollback) { throw new AggregateException(error, rollback); }
                throw;
            }
            RaiseModsChanged();
            return new(id, true, transaction.BackupPath);
        }
        finally { _mutationGate.Release(); }
    }
}
