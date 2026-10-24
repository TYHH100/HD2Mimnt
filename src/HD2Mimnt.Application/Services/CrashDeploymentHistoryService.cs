using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

public sealed class CrashDeploymentHistoryService(ICrashHistoryRepository repository)
{
    public Task<IReadOnlyList<CrashDeploymentSnapshot>> GetRecentAsync(CancellationToken token = default) => repository.GetRecentAsync(token);
    public Task MarkKnownGoodAsync(Guid sessionId, CancellationToken token = default) => repository.MarkKnownGoodAsync(sessionId, token);
    public Task SaveAsync(CrashDeploymentSnapshot snapshot, CancellationToken token = default) => repository.SaveAsync(snapshot, token);

    public Task<CrashDeploymentSnapshot> CaptureAsync(Guid sessionId, Profile profile, string gamePath, DateTime startedUtc,
        IReadOnlyList<(Mod Mod, string OptionsJson, IReadOnlyList<string> Files)> selected, CancellationToken token = default) =>
        Task.Run(() =>
        {
            var snapshots = new List<CrashModSnapshot>();
            foreach (var (mod, options, files) in selected)
            {
                token.ThrowIfCancellationRequested();
                var metadata = new StringBuilder();
                foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var info = new FileInfo(path);
                    metadata.Append(Path.GetRelativePath(mod.Path, path)).Append('|').Append(info.Length).Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                snapshots.Add(new(mod.Id, mod.Name, mod.Version, mod.Hash, mod.ModifiedAt,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata.ToString()))),
                    ProfileModOptionResolver.ResolveEffective(mod.Manifest, options).ToJson(), snapshots.Count));
            }
            return new CrashDeploymentSnapshot(sessionId, profile.Id, profile.Name, Path.GetFullPath(gamePath), startedUtc,
                null, "Deploying", false, snapshots);
        }, token);

    public async Task<CrashDeploymentComparison?> CompareAsync(DateTime capturedUtc, string gamePath, CancellationToken token = default)
    {
        var history = (await repository.GetRecentAsync(token).ConfigureAwait(false))
            .Where(snapshot => snapshot.StartedUtc <= capturedUtc && SamePath(snapshot.GamePath, gamePath))
            .OrderByDescending(snapshot => snapshot.StartedUtc).ToArray();
        var current = history.FirstOrDefault();
        if (current == null) return null;
        var older = history.Skip(1).Where(snapshot => snapshot.Status == "Completed" && snapshot.EndedUtc <= capturedUtc).ToArray();
        var baseline = older.FirstOrDefault(snapshot => snapshot.KnownGood)
            ?? older.FirstOrDefault(snapshot => snapshot.SourceSessionId == null) ?? older.FirstOrDefault();
        if (current.EndedUtc == null || current.EndedUtc > capturedUtc)
            current = current with { Status = "InProgressAtCrash" };
        return new(current, baseline, baseline == null ? [] : Compare(baseline.Mods, current.Mods));
    }

    public async Task RecordRemovalAsync(Guid sessionId, IReadOnlyCollection<Guid>? removedModIds, CancellationToken token = default)
    {
        var previous = (await repository.GetRecentAsync(token).ConfigureAwait(false))
            .FirstOrDefault(snapshot => snapshot.SessionId == sessionId || snapshot.SourceSessionId == sessionId);
        if (previous == null) return;
        var now = DateTime.UtcNow;
        await repository.SaveAsync(previous with
        {
            SessionId = Guid.NewGuid(), SourceSessionId = sessionId, StartedUtc = now, EndedUtc = now, Status = "Completed", KnownGood = false,
            Mods = removedModIds == null ? [] : previous.Mods.Where(mod => !removedModIds.Contains(mod.ModId)).ToArray()
        }, token).ConfigureAwait(false);
    }

    public static IReadOnlyList<CrashModChange> Compare(IReadOnlyList<CrashModSnapshot> baseline, IReadOnlyList<CrashModSnapshot> current)
    {
        var old = baseline.ToDictionary(mod => mod.ModId);
        var present = current.Select(mod => mod.ModId).ToHashSet();
        // 新增/删除引起的索引平移不算顺序变更，只比较共同成员间的相对顺序。
        var beforeOrder = baseline.OrderBy(mod => mod.Order).Where(mod => present.Contains(mod.ModId)).Select(mod => mod.ModId).ToArray();
        var afterOrder = current.OrderBy(mod => mod.Order).Where(mod => old.ContainsKey(mod.ModId)).Select(mod => mod.ModId).ToArray();
        var changes = new List<CrashModChange>();
        foreach (var mod in current)
        {
            var kinds = new List<CrashModChangeKind>();
            if (!old.TryGetValue(mod.ModId, out var previous)) kinds.Add(CrashModChangeKind.Added);
            else
            {
                if (previous.Version != mod.Version || previous.Hash != mod.Hash || previous.ModifiedUtc != mod.ModifiedUtc
                    || (previous.OptionsJson == mod.OptionsJson && previous.FileMetadataHash != mod.FileMetadataHash)) kinds.Add(CrashModChangeKind.Updated);
                if (previous.OptionsJson != mod.OptionsJson) kinds.Add(CrashModChangeKind.OptionsChanged);
                if (Array.IndexOf(beforeOrder, mod.ModId) != Array.IndexOf(afterOrder, mod.ModId)) kinds.Add(CrashModChangeKind.OrderChanged);
            }
            if (kinds.Count > 0) changes.Add(new(mod.ModId, mod.Name, kinds));
        }
        changes.AddRange(baseline.Where(mod => !present.Contains(mod.ModId)).Select(mod =>
            new CrashModChange(mod.ModId, mod.Name, [CrashModChangeKind.Removed])));
        return changes;
    }

    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
