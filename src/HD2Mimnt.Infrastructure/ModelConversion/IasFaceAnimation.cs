using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasFaceTarget(
    string Name,
    int Parent,
    string? ParentName,
    int SourceIndex,
    string? SourceName,
    int PaletteSlot,
    uint Flags,
    double TranslationScale,
    IasPose RestLocal);

public sealed record IasFaceProfile(
    string UnitId,
    int TargetBoneCount,
    IReadOnlyList<IasFaceTarget> EyeTargets,
    IReadOnlyList<IasFaceTarget> EyelidOrBlinkTargets,
    IReadOnlyList<IasFaceTarget> FacialTargets);

public sealed record IasFaceAnimation(
    IReadOnlyList<IasFaceProfile> Profiles,
    IReadOnlyList<IasMorphProgram> MorphPrograms,
    int EyeTargetCount,
    int EyelidOrBlinkTargetCount,
    string EyeTracking,
    string Blinking);

/// <summary>原面部骨骼只按名字审计；不声称已具备运行时眨眼/眼动能力。</summary>
public static class IasFaceAnimationAudit
{
    private static readonly Regex Spaced = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex Tokens = new("[a-z0-9]+|[\\u3040-\\u30ff\\u3400-\\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex EyeCompact = new("^(left|right|l|r|c|center)?(eye|eyes|eyeball)(left|right|l|r|c|center)?$", RegexOptions.Compiled);

    private static readonly HashSet<string> EyelidTokens = new(StringComparer.Ordinal)
        { "eyelids", "blink", "eyelashes", "blinking", "lid", "eyelash", "eyelid" };
    private static readonly HashSet<string> EyeTokens = new(StringComparer.Ordinal) { "eyes", "eye", "eyeball", "ocular" };
    private static readonly HashSet<string> FacialTokens = new(StringComparer.Ordinal)
        { "lips", "mouth", "brow", "tongue", "cheek", "lip", "jaw", "brows", "face" };

    public static IasFaceAnimation Audit(IReadOnlyDictionary<string, byte[]> members, IReadOnlyList<IasSourceBone> sources,
        IReadOnlyDictionary<string, IasRigProfile> profiles, bool morphEnabled)
    {
        var morphPrograms = morphEnabled
            ? members.Where(entry => entry.Key.EndsWith(".hd2morph", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new IasMorphProgram(entry.Key, IasHash.Sha256Hex(entry.Value), entry.Value.Length)).ToList()
            : [];
        var rows = new List<IasFaceProfile>();
        foreach (var (uid, profile) in profiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var nodes = profile.TargetBones;
            var eyes = new List<IasFaceTarget>();
            var eyelids = new List<IasFaceTarget>();
            var facial = new List<IasFaceTarget>();
            foreach (var node in nodes)
            {
                var (isEye, isLid, isFacial) = Kind(node.Name);
                if (!isFacial) continue;
                var row = new IasFaceTarget(node.Name, node.Parent + 1,
                    node.Parent >= 0 ? nodes[node.Parent].Name : null,
                    node.SourceIndex + 1,
                    node.SourceIndex >= 0 && node.SourceIndex < sources.Count ? sources[node.SourceIndex].Name : null,
                    node.PaletteSlot, node.Flags, node.TranslationScale, IasAffine.Pose(node.RestLocal));
                facial.Add(row);
                if (isEye) eyes.Add(row);
                if (isLid) eyelids.Add(row);
            }
            if (facial.Count > 0) rows.Add(new IasFaceProfile(uid, nodes.Count, eyes, eyelids, facial));
        }
        var eyeCount = rows.Sum(row => row.EyeTargets.Count);
        var lidCount = rows.Sum(row => row.EyelidOrBlinkTargets.Count);
        return new IasFaceAnimation(rows, morphPrograms, eyeCount, lidCount,
            eyeCount > 0 ? "named_eye_targets_present" : "no_named_eye_targets",
            morphPrograms.Count > 0 ? "native_morph_conversion_available_runtime_verification_pending"
                : lidCount > 0 ? "eyelid_or_blink_targets_present_runtime_unverified"
                : "not_determined_from_rig_names");
    }

    private static (bool Eye, bool Eyelid, bool Facial) Kind(string name)
    {
        var spaced = Spaced.Replace(name, "$1 $2");
        var tokens = Tokens.Matches(spaced.ToLowerInvariant()).Select(match => match.Value).ToList();
        var compact = string.Concat(tokens);
        var eyelid = tokens.Any(token => EyelidTokens.Contains(token))
                     || new[] { "eyelid", "eyelash", "blink", "まばたき", "眼皮", "睫毛", "眨眼" }.Any(compact.Contains);
        var eye = !eyelid && (tokens.Any(token => EyeTokens.Contains(token))
                              || EyeCompact.IsMatch(compact)
                              || new[] { "左目", "右目", "両目", "目", "左眼", "右眼", "眼球", "左眼球", "右眼球" }.Contains(compact));
        var facial = eye || eyelid || tokens.Any(token => FacialTokens.Contains(token))
                     || new[] { "顔", "顎", "眉", "口", "頬", "舌" }.Any(compact.Contains);
        return (eye, eyelid, facial);
    }
}
