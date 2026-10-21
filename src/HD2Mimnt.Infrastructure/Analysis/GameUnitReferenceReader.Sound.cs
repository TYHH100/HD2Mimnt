using System.Security.Cryptography;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class GameUnitReferenceReader
{
    public async Task<IReadOnlyDictionary<ulong, SoundBankReference>> ReadSoundBanksAsync(string gameDirectory,
        IReadOnlyCollection<ulong> resourceIds, CancellationToken cancellationToken = default)
    {
        if (resourceIds.Count == 0) return new Dictionary<ulong, SoundBankReference>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => ReadSoundBanksCore(gameDirectory, resourceIds, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private IReadOnlyDictionary<ulong, SoundBankReference> ReadSoundBanksCore(string directory,
        IReadOnlyCollection<ulong> ids, CancellationToken token)
    {
        var requested = ids.ToHashSet();
        var matches = requested.ToDictionary(id => id, _ => new Dictionary<string, byte[]>());
        var errors = new Dictionary<ulong, string>();
        string? globalError = null;
        try
        {
            var snapshot = GetSnapshot(directory, token);
            using var session = new ArchiveSession(snapshot, token);
            foreach (var package in snapshot.Packages)
            {
                token.ThrowIfCancellationRequested();
                if (!IsMainPackage(package.Name) || !requested.Any(id => FilterContains(package.Filter, id))) continue;
                var (toc, start, count) = session.ReadToc(package);
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var offset = start + i * 80;
                    var id = U64(toc, offset);
                    if (!requested.Contains(id) || U64(toc, offset + 8) != SoundFix.SoundBankRepair.BankType) continue;
                    try
                    {
                        var size = U32(toc, offset + 56);
                        Require(size is >= 16 and <= MaxResourceBytes, "游戏音库尺寸无效。");
                        var data = session.ReadRange(package.Items, U64(toc, offset + 16), (int)size);
                        var bank = data[16..];
                        matches[id].TryAdd(Convert.ToHexString(SHA256.HashData(bank)), bank);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
                    { errors[id] = ex.Message; }
                }
            }
            Require(snapshot.Fingerprint == GetFingerprint(directory), "游戏归档在读取期间发生变化。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { globalError = ex.Message; }
        return matches.ToDictionary(pair => pair.Key, pair =>
            globalError is null && !errors.ContainsKey(pair.Key) && pair.Value.Count == 1
                ? new SoundBankReference(pair.Value.Values.First())
                : new SoundBankReference(null, globalError ?? errors.GetValueOrDefault(pair.Key)
                    ?? (pair.Value.Count == 0 ? "当前游戏中未找到对应音库。" : "当前游戏音库存在多个不同版本，无法确定参考。")));
    }
}
