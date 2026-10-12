namespace HD2Mimnt.UI.ViewModels;

/// <summary>同一组关联模组共享成员数组，避免每条资源为每张卡片重复展开关系。</summary>
public sealed class ConflictCardIndex
{
    public static ConflictCardIndex Empty { get; } = new();
    private readonly Dictionary<Guid, List<ConflictModTarget[]>> _setsByMod = new();

    public static ConflictCardIndex Create(IEnumerable<ConflictGroupViewModel> groups, CancellationToken token = default)
    {
        var index = new ConflictCardIndex();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var members = group.Owners.DistinctBy(owner => owner.ModId).OrderBy(owner => owner.ModId)
                .Select(owner => new ConflictModTarget(owner.ModId, owner.ModName)).ToArray();
            if (members.Length < 2 || !seen.Add(string.Join('/', members.Select(member => member.ModId)))) continue;
            foreach (var member in members)
            {
                if (!index._setsByMod.TryGetValue(member.ModId, out var sets))
                    index._setsByMod[member.ModId] = sets = new();
                sets.Add(members);
            }
        }
        return index;
    }

    public bool Contains(Guid id) => _setsByMod.ContainsKey(id);

    public IEnumerable<ConflictModTarget> GetTargets(Guid id) => _setsByMod.TryGetValue(id, out var sets)
        ? sets.SelectMany(set => set).Where(target => target.ModId != id).DistinctBy(target => target.ModId)
        : [];
}
