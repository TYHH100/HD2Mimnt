using System.Security.Cryptography;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class GameUnitReferenceReader
{
    public async Task<IReadOnlyList<GameCompanionResult>> ReadCompanionsAsync(string gameDirectory,
        IReadOnlyList<GameCompanionRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => ReadCompanionsCore(gameDirectory, requests, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<GameCompanionResult> ReadCompanionsCore(string gameDirectory,
        IReadOnlyList<GameCompanionRequest> requests, CancellationToken token)
    {

        var results = requests.Select(_ => new Dictionary<string, byte[]>()).ToArray();
        var errors = new Dictionary<int, string>();
        string? globalError = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = GetSnapshot(gameDirectory, token);
            using var session = new ArchiveSession(snapshot, token);
            var packages = snapshot.Packages.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var needed = requests.Select((request, i) => (request, i)).GroupBy(pair => (pair.request.ResourceId, pair.request.TypeId))
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var (name, package) in packages)
            {
                token.ThrowIfCancellationRequested();
                if (!IsMainPackage(name) || !requests.Any(r => FilterContains(package.Filter, r.ResourceId))) continue;
                var (toc, entryStart, count) = session.ReadToc(package);
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = entryStart + i * 80;
                    if (!needed.TryGetValue((U64(toc, entry), U64(toc, entry + 8)), out var matching)) continue;
                    foreach (var (request, requestIndex) in matching)
                    {
                        if (U32(toc, entry + 56) != request.MainData.Length
                            || U32(toc, entry + (request.Gpu ? 64 : 60)) != request.Size) continue;
                        try
                        {
                            var main = session.ReadRange(package.Items, U64(toc, entry + 16), request.MainData.Length);
                            if (!main.AsSpan().SequenceEqual(request.MainData)) continue;
                            Require(packages.TryGetValue(name + (request.Gpu ? ".gpu_resources" : ".stream"), out var companionPackage),
                                "游戏包缺少对应伴随映射。");
                            Require(request.Size is > 0 and <= MaxResourceBytes, "伴随片段过大。");
                            var payload = session.ReadRange(companionPackage!.Items, U64(toc, entry + (request.Gpu ? 32 : 24)), (int)request.Size);
                            results[requestIndex].TryAdd(Convert.ToHexString(SHA256.HashData(payload)), payload);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
                        { errors[requestIndex] = ex.Message; }
                    }
                }
            }
            Require(snapshot.Fingerprint == GetFingerprint(gameDirectory), "游戏归档在读取期间发生变化。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { globalError = ex.Message; }

        return results.Select((payloads, i) => new GameCompanionResult
        {
            Payload = globalError is null && !errors.ContainsKey(i) && payloads.Count == 1 ? payloads.Values.First() : null,
            Error = globalError ?? errors.GetValueOrDefault(i)
                ?? (payloads.Count == 1 ? null : payloads.Count == 0 ? "未找到逐字节一致的游戏资源。" : "游戏伴随载荷存在歧义。")
        }).ToArray();
    }

}
