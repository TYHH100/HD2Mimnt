using System.Text;
using HD2Mimnt.ModelConversion.Gmod.Geometry;

namespace HD2Mimnt.ModelConversion.Gmod.Parts;

/// <summary>HD2 把护甲拆成身体与头盔两套 Unit，而 GMOD 模型把头与身体放在同一个 MDL 里。</summary>
public enum GmodPartKind { Body, Helmet }

[Flags]
public enum GmodPartMask
{
    None = 0,
    Body = 1,
    Helmet = 2,
    Both = Body | Helmet
}

/// <summary>
/// 单次转换产出的部位。None 表示按整个模型输出一份（兼容旧行为）。
/// </summary>
public enum GmodPartSplit { Both, Body, Helmet, None }

/// <summary>
/// 头部骨骼子树 + 每个网格/每个 bodygroup 的部位归属。
/// 网格归属按顶点骨骼权重的主导骨骼判定：主导骨骼在头部子树内 → 头盔，否则 → 身体。
/// </summary>
public sealed record GmodPartSplitResult(
    IReadOnlySet<int> HeadBones,
    IReadOnlyDictionary<string, GmodPartKind> MeshParts,
    IReadOnlyDictionary<int, GmodPartMask> BodyGroupMasks,
    IReadOnlyList<GmodConversionIssue> Issues)
{
    public bool HasBody => MeshParts.Values.Any(part => part == GmodPartKind.Body);
    public bool HasHelmet => MeshParts.Values.Any(part => part == GmodPartKind.Helmet);

    /// <summary>该部位的网格 id 集合。</summary>
    public IReadOnlySet<string> MeshIds(GmodPartKind kind) => MeshParts
        .Where(entry => entry.Value == kind).Select(entry => entry.Key)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 该部位需要发布的开关。skin 与所有备选都落在该部位的 bodygroup 才算，避免头盔包出现纯身体开关。
    /// </summary>
    public IReadOnlySet<string> AxisKeys(GmodPartKind kind, bool includeSkin)
    {
        var bit = kind == GmodPartKind.Helmet ? GmodPartMask.Helmet : GmodPartMask.Body;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in BodyGroupMasks)
        {
            if ((entry.Value & bit) != 0) keys.Add($"bodygroup:{entry.Key}");
        }
        if (includeSkin) keys.Add("skin");
        return keys;
    }
}

/// <summary>按骨骼权重把 GMOD 模型的网格拆成 HD2 的身体与头盔两部分。</summary>
public static class GmodPartSplitter
{
    private static readonly string[] HeadBoneHints = ["head", "neck"];

    public static GmodPartSplitResult Split(GmodMeshSet meshSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meshSet);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<GmodConversionIssue>();
        var headBones = DetectHeadBones(meshSet.Bones);
        if (headBones.Count == 0)
        {
            issues.Add(new GmodConversionIssue("HeadBoneNotFound", meshSet.ModelPath,
                "No head bone could be identified in the skeleton, so no helmet part is produced. " +
                "Every mesh is treated as body armour; map the head bone explicitly if this model names it differently.",
                GmodIssueSeverity.Warning));
        }

        var meshParts = new Dictionary<string, GmodPartKind>(StringComparer.Ordinal);
        foreach (var mesh in meshSet.Meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            meshParts[mesh.Id] = headBones.Count > 0 && DominantBoneInHead(mesh, headBones)
                ? GmodPartKind.Helmet
                : GmodPartKind.Body;
        }

        // bodygroup 归属：备选里只要出现该部位的网格，这个开关就要出现在该部位的包里。
        var bodyGroups = new Dictionary<int, GmodPartMask>();
        foreach (var mesh in meshSet.Meshes)
        {
            var mask = meshParts[mesh.Id] == GmodPartKind.Helmet ? GmodPartMask.Helmet : GmodPartMask.Body;
            bodyGroups[mesh.BodyPart] = bodyGroups.TryGetValue(mesh.BodyPart, out var existing) ? existing | mask : mask;
        }

        var helmetMeshes = meshParts.Count(entry => entry.Value == GmodPartKind.Helmet);
        if (helmetMeshes > 0)
        {
            issues.Add(new GmodConversionIssue("PartSplitApplied", meshSet.ModelPath,
                $"Split into body and helmet: {helmetMeshes} helmet mesh(es) skinned to the head subtree, " +
                $"{meshParts.Count - helmetMeshes} body mesh(es).", GmodIssueSeverity.Info));
        }
        else if (headBones.Count > 0)
        {
            issues.Add(new GmodConversionIssue("NoHeadGeometry", meshSet.ModelPath,
                "A head bone exists but no mesh is skinned to it, so only a body bundle is produced.",
                GmodIssueSeverity.Warning));
        }

        return new GmodPartSplitResult(headBones, meshParts.AsReadOnly(), bodyGroups.AsReadOnly(), issues.AsReadOnly());
    }

    /// <summary>骨骼名归一化（去掉分隔符、小写）后匹配 head/neck 线索，并扩展其全部子骨。</summary>
    public static IReadOnlySet<int> DetectHeadBones(IReadOnlyList<GmodBone> bones)
    {
        var result = new HashSet<int>();
        var roots = new List<int>();
        foreach (var bone in bones)
        {
            var normalized = Normalize(bone.Name);
            if (normalized.Length == 0) continue;
            if (HeadBoneHints.Any(hint => normalized.Contains(hint, StringComparison.Ordinal))) roots.Add(bone.Index);
        }
        if (roots.Count == 0) return result;

        // 只保留最靠上的根（例如同时命中 head 与 neck 时以 head 为准），再展开子树。
        var topMost = roots.Where(index => !HasAncestorIn(index, roots, bones)).Order().ToArray();
        var children = new Dictionary<int, List<int>>();
        foreach (var bone in bones)
        {
            if (bone.Parent < 0) continue;
            if (!children.TryGetValue(bone.Parent, out var list)) children[bone.Parent] = list = [];
            list.Add(bone.Index);
        }

        var stack = new Stack<int>(topMost);
        while (stack.TryPop(out var index))
        {
            if (!result.Add(index) || !children.TryGetValue(index, out var list)) continue;
            foreach (var child in list) stack.Push(child);
        }
        return result;
    }

    private static bool HasAncestorIn(int index, IReadOnlyList<int> roots, IReadOnlyList<GmodBone> bones)
    {
        for (var parent = bones[index].Parent; parent >= 0 && parent < bones.Count; parent = bones[parent].Parent)
        {
            if (roots.Contains(parent)) return true;
            if (parent == index) break;
        }
        return false;
    }

    private static bool DominantBoneInHead(GmodMesh mesh, IReadOnlySet<int> headBones)
    {
        var weights = new Dictionary<int, double>();
        foreach (var vertex in mesh.Vertices)
        {
            for (var lane = 0; lane < vertex.Bones.Length && lane < vertex.Weights.Length; lane++)
            {
                var bone = vertex.Bones[lane];
                var weight = vertex.Weights[lane];
                if (weight <= 0) continue;
                weights[bone] = weights.GetValueOrDefault(bone) + weight;
            }
        }
        if (weights.Count == 0) return false;
        var dominant = weights.MaxBy(entry => entry.Value).Key;
        return headBones.Contains(dominant);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }
}
