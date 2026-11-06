using System.Globalization;
using System.Numerics;
using System.Text;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.ModelConversion.Gmod.Rig;

/// <summary>
/// 依据 Source 骨架生成 HD2IRG1：每个已发布 Unit 一份 profile。
/// 沿用 AQSDK 的 scene-node 约定（16 字节零 needle、needle_to_palette=0、
/// scene_graph_index + source_parent_space + transfer_translation）。
/// </summary>
public static class GmodRigBuilder
{
    private const int MaxBones = 256;

    public static (IasRigDocument Rig, IReadOnlyList<GmodConversionIssue> Issues) Build(
        IReadOnlyList<GmodBone> sourceBones, IReadOnlyList<ulong> publishedUnitIds,
        GmodConversionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<GmodConversionIssue>();
        if (publishedUnitIds.Count == 0)
            throw new IasFormatException("A rig profile requires at least one published Unit.");
        var count = Math.Min(sourceBones.Count, MaxBones);
        if (sourceBones.Count > MaxBones)
            issues.Add(new GmodConversionIssue("RigBoneBudget", "skeleton",
                $"HD2IRG1 exports the first {MaxBones} of {sourceBones.Count} bones; the full skeleton remains in the geometry bundle."));
        if (count == 0)
            throw new IasFormatException("The Source model has no bones to export into a rig.");

        var overrides = options.BoneNameOverrides;
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<IasRigSourceBone>(count);
        var targetNames = new string[count];
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bone = sourceBones[i];
            var sourceName = Unique(Sanitize(bone.Name, i, "source"), usedNames);
            var parent = bone.Parent >= 0 && bone.Parent < i ? bone.Parent : -1;
            sources.Add(new IasRigSourceBone(sourceName, parent,
                parent < 0 ? IasRigSourceFlags.Root : IasRigSourceFlags.None, RestLocal(bone)));

            var mapped = overrides is not null && overrides.TryGetValue(bone.Name, out var custom)
                ? custom
                : ValveBipedMapping.Defaults.GetValueOrDefault(bone.Name);
            targetNames[i] = Sanitize(mapped ?? bone.Name, i, "target");
        }

        // Target names must be unique inside a profile; collisions keep an explicit numeric suffix.
        var targetUsed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < targetNames.Length; i++)
            targetNames[i] = Unique(targetNames[i], targetUsed);

        var needle = options.RigNeedle ?? new byte[16];
        if (needle.Length < 16)
            throw new IasFormatException("A custom rig needle must contain at least 16 bytes.");

        var targets = new IasRigTargetBone[count];
        for (var i = 0; i < count; i++)
        {
            var bone = sourceBones[i];
            var parent = bone.Parent >= 0 && bone.Parent < i ? bone.Parent : -1;
            targets[i] = new IasRigTargetBone(targetNames[i], parent, i, (uint)i,
                IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.SourceParentSpace |
                IasRigTargetFlags.TransferTranslation, 1f, RestLocal(bone));
        }

        var profiles = publishedUnitIds.Distinct().OrderBy(id => id).Select(unitId => new IasRigProfile(
            unitId, targets, (byte[])needle.Clone(), 0, (uint)count)).ToArray();
        var rig = new IasRigDocument(sources.AsReadOnly(), profiles);
        // Validate once here so an invalid skeleton is reported before companion files are written.
        _ = IasRigCodec.Write(rig, cancellationToken);
        issues.Add(new GmodConversionIssue("RigRestPoseFromSource", "rigbin",
            "Rig profile rest_local matrices come from the Source skeleton. They must be reconciled with the target HD2 unit's scene graph when the .unit mesh is authored.",
            GmodIssueSeverity.Info));
        if (options.RigNeedle is null)
            issues.Add(new GmodConversionIssue("RigNeedleZeroFilled", "rigbin",
                "A zero-filled 16-byte needle and needle_to_palette=0 follow the AQSDK scene-node authoring route; GPU palette discovery is disabled.",
                GmodIssueSeverity.Info));
        return (rig, issues.AsReadOnly());
    }

    /// <summary>与 Unit 的 TransformInfo 共用同一份 rest 局部变换实现。</summary>
    private static Matrix4x4 RestLocal(GmodBone bone) => GmodBonePose.Local(bone);

    private static string Sanitize(string name, int index, string role)
    {
        var safe = new string(name.Select(ch => char.IsControl(ch) || ch == '\0' ? '_' : ch).ToArray()).Trim();
        if (safe.Length == 0) return $"{role}_{index.ToString(CultureInfo.InvariantCulture)}";
        if (Encoding.UTF8.GetByteCount(safe) <= 63) return safe;
        var result = new StringBuilder();
        foreach (var ch in safe)
        {
            if (Encoding.UTF8.GetByteCount(result.ToString() + ch) > 58) break;
            result.Append(ch);
        }
        return result.Append('_').Append(index.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (var suffix = 1; ; suffix++)
        {
            var suffixText = "_" + suffix.ToString(CultureInfo.InvariantCulture);
            var candidate = name.Length + suffixText.Length <= 63
                ? name + suffixText
                : name[..(63 - suffixText.Length)] + suffixText;
            if (used.Add(candidate)) return candidate;
        }
    }
}
