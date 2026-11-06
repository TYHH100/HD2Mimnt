using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Gmod.Physics;

/// <summary>
/// 把 Source 的 <c>$jigglebone</c> / <c>BONE_PHYSICALLY_SIMULATED</c> 骨链转成 HD2PHY1 弹簧链。
/// 所有 jigglebone 的 34 个原始参数同时写入几何包与 jiggle-parameters.json，不会因格式差异丢失。
/// </summary>
public static class JiggleBoneConverter
{
    private const int MaxBones = 256;
    private const float DegreesToRadians = MathF.PI / 180f;

    public sealed record Result(
        IReadOnlyList<Hd2PhysBone> Bones,
        IReadOnlyList<Hd2PhysChain> Chains,
        uint PaletteSlots,
        IReadOnlyList<GmodConversionIssue> Issues);

    public static Result Convert(IReadOnlyList<GmodBone> sourceBones, ReadOnlyMemory<byte>? physicsData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<GmodConversionIssue>();
        var count = Math.Min(sourceBones.Count, MaxBones);
        if (sourceBones.Count > MaxBones)
            issues.Add(new GmodConversionIssue("PhysicsBoneBudget", "skeleton",
                $"The Source skeleton has {sourceBones.Count} bones; HD2PHY1 exports the first {MaxBones}. All bones remain in the geometry bundle."));

        var globalRotation = new Quaternion[sourceBones.Count];
        var globalPosition = new Vector3[sourceBones.Count];
        for (var i = 0; i < sourceBones.Count; i++)
        {
            var bone = sourceBones[i];
            var rotation = Normalize(bone.Rotation);
            if (bone.Parent < 0 || bone.Parent >= i)
            {
                globalRotation[i] = rotation;
                globalPosition[i] = bone.Position;
            }
            else
            {
                globalRotation[i] = Quaternion.Multiply(globalRotation[bone.Parent], rotation);
                globalPosition[i] = globalPosition[bone.Parent] +
                    Vector3.Transform(bone.Position, globalRotation[bone.Parent]);
            }
        }

        var bones = new List<Hd2PhysBone>(count);
        for (var i = 0; i < count; i++)
        {
            var bone = sourceBones[i];
            var parentSlot = bone.Parent >= 0 && bone.Parent < count ? bone.Parent : -1;
            bones.Add(new Hd2PhysBone(TruncateName(bone.Name, i), i, parentSlot, globalPosition[i], Basis(globalRotation[i])));
        }

        var physicsSet = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var bone = sourceBones[i];
            if (bone.Jiggle is not null || (bone.Flags & (uint)MdlBoneFlags.PhysicallySimulated) != 0 ||
                bone.ProceduralType == (int)MdlProceduralBoneType.Jiggle)
                physicsSet.Add(i);
            else if (bone.ProceduralType != 0)
                issues.Add(new GmodConversionIssue("ProceduralBoneMetadataOnly", bone.Name,
                    $"Procedural bone type {bone.ProceduralType} (non-jiggle) has no HD2PHY1 spring equivalent; " +
                    "its flags and rest pose are preserved in the geometry bundle.", GmodIssueSeverity.Info));
        }

        var consumed = new HashSet<int>();
        var chains = new List<Hd2PhysChain>();
        foreach (var rootIndex in physicsSet.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumed.Contains(rootIndex)) continue;
            var path = new List<int> { rootIndex };
            var current = rootIndex;
            while (true)
            {
                var child = physicsSet.Where(index => index > current && !consumed.Contains(index) &&
                    sourceBones[index].Parent == current).Order().FirstOrDefault(-1);
                if (child < 0) break;
                path.Add(child);
                current = child;
            }
            foreach (var index in path) consumed.Add(index);

            var driver = sourceBones[rootIndex].Parent;
            var hasDriver = driver >= 0 && driver < count && !physicsSet.Contains(driver);
            var jointIndices = hasDriver ? new[] { driver }.Concat(path).ToArray() : path.ToArray();
            if (jointIndices.Length < 2)
            {
                issues.Add(new GmodConversionIssue("PhysicsChainTooShort", sourceBones[rootIndex].Name,
                    "A single physics bone without a fixed parent cannot form an HD2PHY1 chain (2 joints required)."));
                continue;
            }
            var valid = true;
            for (var i = 1; i < jointIndices.Length; i++)
            {
                if (Vector3.Distance(globalPosition[jointIndices[i - 1]], globalPosition[jointIndices[i]]) < 1e-5f)
                {
                    valid = false;
                    issues.Add(new GmodConversionIssue("PhysicsZeroLengthSegment", sourceBones[jointIndices[i]].Name,
                        "Coincident rest heads cannot form an HD2PHY1 spring segment; this bone is preserved as metadata only."));
                    break;
                }
            }
            if (!valid) continue;

            var primary = path.Select(index => sourceBones[index].Jiggle).FirstOrDefault(jiggle => jiggle is not null);
            var stiffness = primary is null ? 8f : Math.Clamp(
                (primary.YawStiffness + primary.PitchStiffness + primary.AlongStiffness) / 3f, 0.001f, 1000f);
            var damping = primary is null ? 0.25f : Math.Clamp(
                (primary.YawDamping + primary.PitchDamping + primary.AlongDamping) / 3f, 0f, 1f);
            if (primary is not null && (primary.Flags & MdlJiggleFlags.HasBaseSpring) != 0 && primary.BaseStiffness > 0)
                stiffness = Math.Clamp((stiffness + primary.BaseStiffness) / 2f, 0.001f, 1000f);

            var joints = jointIndices.Select((index, position) =>
            {
                var jiggle = position == 0 && hasDriver ? null : sourceBones[index].Jiggle;
                return Joint(sourceBones[index], jiggle, hasDriver && position == 0);
            }).ToArray();
            var chainName = TruncateName($"chain_{rootIndex}_{sourceBones[rootIndex].Name}", rootIndex);
            chains.Add(new Hd2PhysChain(chainName, joints, stiffness, damping,
                new Vector3(0, 0, -9.81f), Vector3.Zero, 1, uint.MaxValue,
                RootSwing: !hasDriver, LocalBend: primary is not null && (primary.Flags & MdlJiggleFlags.IsFlexible) != 0));
        }

        if (physicsData is { Length: > 0 } raw)
        {
            var solidCount = ReadPhySolidCount(raw.Span);
            issues.Add(new GmodConversionIssue("RagdollCollisionRetainedRaw", "model.phy",
                $"The Source .phy ragdoll/collision data ({raw.Length} bytes, {solidCount} declared solids) is retained verbatim; " +
                "IVP collision solids are not compiled into HD2PHY1 colliders.", GmodIssueSeverity.Info));
        }
        if (physicsSet.Count > 0 && chains.Count == 0)
            issues.Add(new GmodConversionIssue("NoPhysicsChain", "skeleton",
                "Physics bones were found but none could form a valid HD2PHY1 chain; all source parameters are preserved."));

        return new Result(bones.AsReadOnly(), chains.AsReadOnly(), (uint)Math.Max(1, bones.Count), issues.AsReadOnly());
    }

    private static Hd2PhysJoint Joint(GmodBone bone, MdlJiggleBone? jiggle, bool driver)
    {
        if (driver || jiggle is null)
            return new Hd2PhysJoint(TruncateName(bone.Name, bone.Index));
        var angle = jiggle.AngleLimit > 0 ? jiggle.AngleLimit * DegreesToRadians : 30f * DegreesToRadians;
        angle = Math.Max(angle, MathF.Max(MathF.Abs(jiggle.MaxYaw), MathF.Abs(jiggle.MaxPitch)) * DegreesToRadians);
        angle = Math.Clamp(angle, 0.01f, 3.13f);
        var radius = Math.Clamp(jiggle.Length > 0 ? jiggle.Length * 0.02f : 0.02f, 0.001f, 10f);
        var rigidScale = (jiggle.Flags & MdlJiggleFlags.IsRigid) != 0 ? 1.5f : 1f;
        return new Hd2PhysJoint(TruncateName(bone.Name, bone.Index), radius,
            Hd2PhysLimitKind.Cone, angle, angle, new Vector3(1, 0, 0), rigidScale, 1f);
    }

    /// <summary>保守读取 VPHY 头中的 solidCount；不猜测 IVP 内部布局，字节整体保留。</summary>
    private static int ReadPhySolidCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[4..8].SequenceEqual("VPHY"u8)) return -1;
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[12..16]);
        return value is >= 0 and <= 4096 ? value : -1;
    }

    private static float[] Basis(Quaternion rotation)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        return [matrix.M11, matrix.M12, matrix.M13,
            matrix.M21, matrix.M22, matrix.M23,
            matrix.M31, matrix.M32, matrix.M33];
    }

    private static Quaternion Normalize(Quaternion value)
    {
        var length = value.Length();
        return length < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(value);
    }

    private static string TruncateName(string name, int index)
    {
        var safe = new string(name.Select(ch => ch == '\0' ? '_' : ch).ToArray());
        if (Encoding.UTF8.GetByteCount(safe) <= 63) return safe;
        var result = new StringBuilder();
        foreach (var ch in safe)
        {
            if (Encoding.UTF8.GetByteCount(result.ToString() + ch) > 60) break;
            result.Append(ch);
        }
        return result.Append('_').Append(index.ToString(CultureInfo.InvariantCulture)).ToString();
    }
}
