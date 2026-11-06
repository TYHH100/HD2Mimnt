using System.Collections.Frozen;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Source;

public enum BoneMappingStatus { Candidate, PreserveSourceBone, MissingTarget, AmbiguousTarget }
public sealed record BoneMappingCandidate(int SourceIndex, string SourceName, string? TargetName, BoneMappingStatus Status);

public static class ValveBipedMapping
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } = BuildDefaults();

    // Name matches are suggestions only; rest-pose and deformation validation must precede retargeting.
    public static IReadOnlyList<BoneMappingCandidate> Resolve(IReadOnlyList<MdlBone> sourceBones,
        IReadOnlyCollection<string> targetBoneNames, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var mappings = Defaults.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
            foreach (var entry in overrides) mappings[entry.Key] = entry.Value;
        var targets = targetBoneNames.ToHashSet(StringComparer.Ordinal);
        var result = sourceBones.Select(bone =>
        {
            var target = mappings.GetValueOrDefault(bone.Name);
            var status = target is null ? BoneMappingStatus.PreserveSourceBone :
                targets.Contains(target) ? BoneMappingStatus.Candidate : BoneMappingStatus.MissingTarget;
            return new BoneMappingCandidate(bone.Index, bone.Name, target, status);
        }).ToArray();
        var collisions = result.Where(row => row.Status == BoneMappingStatus.Candidate)
            .GroupBy(row => row.TargetName).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return Array.AsReadOnly(result.Select(row => row.Status == BoneMappingStatus.Candidate && collisions.Contains(row.TargetName)
            ? row with { Status = BoneMappingStatus.AmbiguousTarget } : row).ToArray());
    }

    private static FrozenDictionary<string, string> BuildDefaults()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ValveBiped.Bip01_Pelvis"] = "hips", ["ValveBiped.Bip01_Spine"] = "spine1",
            ["ValveBiped.Bip01_Spine1"] = "spine2", ["ValveBiped.Bip01_Spine2"] = "chest",
            ["ValveBiped.Bip01_Neck1"] = "neck", ["ValveBiped.Bip01_Head1"] = "head"
        };
        foreach (var side in new[] { "L", "R" })
        {
            var targetSide = side.ToLowerInvariant();
            foreach (var (source, target) in new[] { ("Clavicle", "clavicle"), ("UpperArm", "shoulder"),
                ("Forearm", "elbow"), ("Hand", "hand"), ("Thigh", "thigh"), ("Calf", "knee"), ("Foot", "foot"), ("Toe0", "ball") })
                names[$"ValveBiped.Bip01_{side}_{source}"] = $"{targetSide}_{target}";
            var fingers = new[] { "thumb", "index", "middle", "ring", "pinky" };
            for (var finger = 0; finger < fingers.Length; finger++)
            for (var joint = 0; joint < 3; joint++)
            {
                var suffix = joint == 0 ? "" : joint.ToString(System.Globalization.CultureInfo.InvariantCulture);
                names[$"ValveBiped.Bip01_{side}_Finger{finger}{suffix}"] = $"{targetSide}_{fingers[finger]}_finger{joint + 1}";
            }
        }
        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
