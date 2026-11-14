namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>上游 <c>companion_sets.read_set</c>：只合并显式选择的多份兼容 IAS 配套。</summary>
public sealed record IasCompanionSet(
    IasCompanionData Data,
    IReadOnlyDictionary<string, byte[]> Members,
    IasRouting Routing);

public static class IasCompanionSets
{
    public static IasCompanionSet Read(IReadOnlyList<IReadOnlyDictionary<string, byte[]>> sets, string seed, bool morphEnabled = true)
    {
        IasBinary.Require(sets.Count > 0, "请选择原模型对应的 IAS 伴随数据。");
        IasCompanionData? first = null;
        var allMembers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var documents = new Dictionary<string, IasRig>(StringComparer.Ordinal);
        var rawProfiles = new Dictionary<string, IasRigProfile>(StringComparer.Ordinal);
        var renderProfiles = new Dictionary<string, IasProfileTable>(StringComparer.Ordinal);
        var groups = new List<IasCompanionGroup>();
        var shared = new List<IasSharedBody>();
        var initial = new Dictionary<string, string>(StringComparer.Ordinal);
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var publishedDomains = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = new List<IasRoutePart>();
        var owners = new List<IasRouteBodyOwner>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var published = new HashSet<string>(StringComparer.Ordinal);
        var membersMetadata = new List<IasCompanionMember>();
        for (var number = 1; number <= sets.Count; number++)
        {
            var members = sets[number - 1];
            var descriptor = members.ContainsKey("mod_group.json") ? null : IasRoutes.ExplicitDescriptor(members, seed);
            var data = IasCompanionReader.Read(members, descriptor, morphEnabled);
            var routing = IasRoutes.Read(members);
            var prefix = sets.Count == 1 ? string.Empty : "companion_" + number + "/";
            if (first is null) first = data;
            else IasBinary.Require(SourcesEqual(first.Sources, data.Sources),
                "多份 IAS 配套使用不同原生骨架，请分别选择对应的模型版本。");
            foreach (var (key, value) in data.RawProfiles)
            {
                IasBinary.Require(!rawProfiles.TryGetValue(key, out var existing) || ProfilesEqual(existing, value),
                    "多份 IAS 配套的骨架数据冲突：" + key);
                rawProfiles[key] = value;
            }
            foreach (var (key, value) in data.RenderProfiles)
            {
                IasBinary.Require(!renderProfiles.TryGetValue(key, out var existing) || TableEquals(existing, value),
                    "多份 IAS 配套的骨架数据冲突：" + key);
                renderProfiles[key] = value;
            }
            foreach (var (key, value) in data.Documents)
            {
                var target = prefix + key;
                IasBinary.Require(!documents.ContainsKey(target), "多份 IAS 配套的骨架数据冲突：" + key);
                documents[target] = value;
            }
            foreach (var group in data.Groups)
            {
                var existing = groups.FirstOrDefault(item => item.Id == group.Id);
                if (existing is not null)
                {
                    IasBinary.Require(GroupEquals(existing, group), "多份 IAS 配套的物理域冲突：" + group.Id);
                    continue;
                }
                groups.Add(group.File is null ? group : group with
                {
                    File = prefix + group.File,
                    OriginalRig = group.OriginalRig is null ? null : prefix + group.OriginalRig
                });
            }
            foreach (var item in data.Shared)
            {
                var existing = shared.FirstOrDefault(value => value.Canonical == item.Canonical);
                if (existing is not null)
                {
                    IasBinary.Require(SharedEquals(existing, item), "多份 IAS 配套的共享身体冲突。");
                    continue;
                }
                shared.Add(item with
                {
                    Rig = prefix + item.Rig,
                    Physics = item.Physics is null ? null : prefix + item.Physics,
                    ConsumerRig = prefix + item.ConsumerRig,
                    PoseFile = item.PoseFile is null ? null : prefix + item.PoseFile
                });
            }
            foreach (var (key, value) in members) allMembers[prefix + key] = value;
            membersMetadata.AddRange(data.Metadata.Members);
            MergeMap(initial, routing.Initial);
            MergeMap(domains, routing.Domains);
            MergeMap(publishedDomains, routing.PublishedDomains);
            foreach (var part in routing.Parts)
            {
                var existing = parts.FirstOrDefault(item => item.Id == part.Id);
                IasBinary.Require(existing is null || existing == part, "多份 IAS 配套的部件开关冲突：" + part.Name);
                if (existing is null) parts.Add(part);
            }
            foreach (var owner in routing.BodyOwners)
                if (!owners.Contains(owner)) owners.Add(owner);
            replaced.UnionWith(routing.Replaced);
            published.UnionWith(routing.Published);
        }
        IasBinary.Require(first is not null, "请选择原模型对应的 IAS 伴随数据。");
        var metadata = first.Metadata with { Members = membersMetadata };
        var merged = new IasRouting(initial, domains, parts, owners,
            replaced.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            published.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            publishedDomains);
        var result = first with
        {
            Metadata = metadata,
            DescriptorFrom = "explicit_original_companions",
            RawProfiles = rawProfiles,
            RenderProfiles = renderProfiles,
            Groups = groups,
            Shared = shared,
            Documents = documents
        };
        return new IasCompanionSet(result, allMembers, merged);
    }

    private static void MergeMap(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (key, value) in source)
        {
            IasBinary.Require(!target.TryGetValue(key, out var existing) || existing == value, "多份 IAS 配套争用同一模型来源：" + key);
            target[key] = value;
        }
    }

    private static bool SourcesEqual(IReadOnlyList<IasProjectedSource> left, IReadOnlyList<IasProjectedSource> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Parent == pair.Second.Parent
            && pair.First.IasFlags == pair.Second.IasFlags
            && PoseEquals(pair.First.LocalRest, pair.Second.LocalRest) && PoseEquals(pair.First.World, pair.Second.World));

    private static bool ProfilesEqual(IasRigProfile left, IasRigProfile right)
        => left.UnitId == right.UnitId && left.PaletteSlots == right.PaletteSlots && left.Flags == right.Flags
           && left.NeedleHex == right.NeedleHex && left.NeedleToPalette == right.NeedleToPalette
           && left.TargetBones.Count == right.TargetBones.Count
           && left.TargetBones.Zip(right.TargetBones).All(pair =>
               pair.First.Name == pair.Second.Name && pair.First.Parent == pair.Second.Parent
               && pair.First.SourceIndex == pair.Second.SourceIndex && pair.First.PaletteSlot == pair.Second.PaletteSlot
               && pair.First.Flags == pair.Second.Flags && pair.First.TranslationScale.Equals(pair.Second.TranslationScale)
               && pair.First.RestLocal.SequenceEqual(pair.Second.RestLocal));

    private static bool TableEquals(IasProfileTable left, IasProfileTable right)
        => left.Nodes.Count == right.Nodes.Count && left.Grips.Count == right.Grips.Count
           && left.Nodes.Zip(right.Nodes).All(pair =>
               pair.First.Name == pair.Second.Name && pair.First.Parent == pair.Second.Parent
               && pair.First.Source == pair.Second.Source && pair.First.Slot == pair.Second.Slot
               && pair.First.Inherit == pair.Second.Inherit && pair.First.Scale.Equals(pair.Second.Scale)
               && pair.First.IasFlags == pair.Second.IasFlags
               && PoseEquals(pair.First.LocalRest, pair.Second.LocalRest) && PoseEquals(pair.First.Rest, pair.Second.Rest))
           && left.Grips.Zip(right.Grips).All(pair =>
               pair.First.Hand == pair.Second.Hand && pair.First.Side == pair.Second.Side
               && PoseEquals(pair.First.Rest, pair.Second.Rest));

    private static bool GroupEquals(IasCompanionGroup left, IasCompanionGroup right)
        => left.Id == right.Id && left.Virtual == right.Virtual && left.AnimationOnly == right.AnimationOnly
           && left.PaletteSlots == right.PaletteSlots && left.PhysicsSha256 == right.PhysicsSha256
           && left.Fingerprint == right.Fingerprint && TableEquals(left.Profile, right.Profile);

    private static bool SharedEquals(IasSharedBody left, IasSharedBody right)
        => left.Id == right.Id && left.Canonical == right.Canonical && left.AnimationOnly == right.AnimationOnly
           && left.Consumers.Count == right.Consumers.Count
           && left.Consumers.All(pair => right.Consumers.TryGetValue(pair.Key, out var other)
                                         && pair.Value.Mapping.SequenceEqual(other.Mapping)
                                         && pair.Value.Slot == other.Slot && pair.Value.Native == other.Native
                                         && pair.Value.Choice == other.Choice
                                         && PoseEquals(pair.Value.Basis, other.Basis));

    private static bool PoseEquals(IasPose left, IasPose right)
        => left.Q.SequenceEqual(right.Q) && left.P.SequenceEqual(right.P);
}
