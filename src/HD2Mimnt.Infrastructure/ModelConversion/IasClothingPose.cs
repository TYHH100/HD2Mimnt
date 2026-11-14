namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasClothingPoseAxis(
    bool Invert,
    bool InvertSecondary,
    int SecondaryAxis,
    IReadOnlyList<double[]> Points);

public sealed record IasClothingPoseRule(
    int Source,
    int Target,
    int Secondary,
    int Reference,
    int SecondaryReference,
    int Mode,
    double Influence,
    double[] Neutral,
    double[] SecondaryNeutral,
    IReadOnlyList<IasClothingPoseAxis> Axes);

public sealed record IasClothingPoseProgram(
    int Version,
    IReadOnlyList<IasClothingPoseRule> Rules,
    IasPose AuthorFromRuntime,
    string SourceSha256,
    string ProfileSha256,
    string OriginalHex);

/// <summary>上游 <c>ias_clothing_pose</c>：读取 HD2POSE1 程序，不改作者 Rig 和曲线。</summary>
public static class IasClothingPose
{
    private const int HeaderSize = 120;
    private const int RuleSize = 60;
    private const int AxisSize = 16;
    private const int PointSize = 16;
    private const int TargetSize = 132;

    public static string ReadUnitId(byte[] raw)
    {
        IasBinary.Require(raw.Length >= HeaderSize && raw.AsSpan(0, 8).SequenceEqual("HD2POSE1"u8), "clothing pose header");
        return $"{IasBinary.ReadUInt64(raw, 32):x16}";
    }

    public static IasPose RigidPose(double[] basis, string context)
    {
        IasBinary.Require(basis.Length == 12 && basis.All(double.IsFinite), context + " is non-finite or incomplete");
        var columns = new double[3][];
        for (var column = 0; column < 3; column++)
        {
            columns[column] = [basis[column], basis[4 + column], basis[8 + column]];
            IasBinary.Require(Math.Abs(Math.Sqrt(columns[column].Sum(value => value * value)) - 1) <= 0.002,
                context + " columns are not unit length");
        }
        foreach (var (left, right) in new[] { (0, 1), (0, 2), (1, 2) })
            IasBinary.Require(Math.Abs(columns[left].Zip(columns[right]).Sum(pair => pair.First * pair.Second)) <= 0.002,
                context + " is not orthogonal");
        var determinant = basis[0] * (basis[5] * basis[10] - basis[6] * basis[9])
                          - basis[1] * (basis[4] * basis[10] - basis[6] * basis[8])
                          + basis[2] * (basis[4] * basis[9] - basis[5] * basis[8]);
        IasBinary.Require(Math.Abs(determinant - 1) <= 0.005, context + " determinant differs from +1");
        return IasAffine.Pose(basis);
    }

    public static IasClothingPoseProgram Read(byte[] raw, IasRigProfile profile)
    {
        IasBinary.Require(raw.Length >= HeaderSize, "clothing pose header");
        var version = IasBinary.ReadUInt32(raw, 8);
        var size = IasBinary.ReadUInt32(raw, 12);
        var length = IasBinary.ReadUInt32(raw, 16);
        var crc = IasBinary.ReadUInt32(raw, 20);
        var count = IasBinary.ReadUInt32(raw, 24);
        var flags = IasBinary.ReadUInt32(raw, 28);
        var uid = IasBinary.ReadUInt64(raw, 32);
        IasBinary.Require(raw[..8].SequenceEqual("HD2POSE1"u8) && version == 1 && size == HeaderSize
                          && length == (uint)raw.Length && flags == 0, "clothing pose header");
        IasBinary.Require(count is >= 1 and <= 64 && IasBinary.Crc32(raw[HeaderSize..]) == crc, "clothing pose count/CRC");
        IasBinary.Require(uid == ulong.Parse(profile.UnitId, System.Globalization.NumberStyles.HexNumber), "clothing pose Unit differs");
        var authorFromRuntime = RigidPose(IasBinary.ReadFloats(raw, 72, 12), "clothing pose basis");
        var bones = profile.TargetBones;
        var fingerprint = raw.AsSpan(40, 32).ToArray();
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(uid));
        payload.AddRange(BitConverter.GetBytes((uint)profile.PaletteSlots));
        payload.AddRange(BitConverter.GetBytes(profile.Flags));
        payload.AddRange(BitConverter.GetBytes((uint)bones.Count));
        foreach (var bone in bones)
        {
            payload.AddRange(IasBinary.FixedName(bone.Name, 64));
            payload.AddRange(BitConverter.GetBytes(bone.Parent));
            payload.AddRange(BitConverter.GetBytes(bone.SourceIndex));
            payload.AddRange(BitConverter.GetBytes((uint)bone.PaletteSlot));
            payload.AddRange(BitConverter.GetBytes(bone.Flags));
            payload.AddRange(BitConverter.GetBytes(bone.TranslationScale));
            foreach (var value in bone.RestLocal) payload.AddRange(BitConverter.GetBytes(value));
        }
        IasBinary.Require(IasHash.Sha256Hex(payload.ToArray()).Equals(Convert.ToHexString(fingerprint).ToLowerInvariant(), StringComparison.Ordinal),
            "clothing pose Rig fingerprint differs");
        var cursor = HeaderSize;
        var rules = new List<IasClothingPoseRule>((int)count);
        var targets = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            IasBinary.Require(cursor + RuleSize <= raw.Length, "clothing pose rule truncated");
            var source = IasBinary.ReadUInt32(raw, cursor);
            var target = IasBinary.ReadUInt32(raw, cursor + 4);
            var secondary = IasBinary.ReadUInt32(raw, cursor + 8);
            var reference = IasBinary.ReadInt32(raw, cursor + 12);
            var secondaryReference = IasBinary.ReadInt32(raw, cursor + 16);
            var mode = IasBinary.ReadUInt32(raw, cursor + 20);
            var influence = IasBinary.ReadSingle(raw, cursor + 24);
            var neutral = IasBinary.ReadFloats(raw, cursor + 28, 8);
            cursor += RuleSize;
            IasBinary.Require(source < bones.Count && target < bones.Count && secondary < bones.Count
                              && reference >= -1 && reference < bones.Count && secondaryReference >= -1 && secondaryReference < bones.Count,
                "clothing pose bone index");
            IasBinary.Require(targets.Add((int)target) && mode < 4 && float.IsFinite(influence) && influence is >= 0 and <= 1,
                "clothing pose duplicate target/mode/influence");
            foreach (var quaternion in new[] { neutral[..4], neutral[4..] })
                IasBinary.Require(quaternion.All(double.IsFinite) && quaternion.Sum(value => value * value) > 1e-16, "clothing pose neutral");
            var axes = new List<IasClothingPoseAxis>(3);
            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                IasBinary.Require(cursor + AxisSize <= raw.Length, "clothing pose axis truncated");
                var axisFlags = IasBinary.ReadUInt32(raw, cursor);
                var secondaryAxis = IasBinary.ReadUInt32(raw, cursor + 4);
                var pointsCount = IasBinary.ReadUInt32(raw, cursor + 8);
                var reserved = IasBinary.ReadUInt32(raw, cursor + 12);
                cursor += AxisSize;
                IasBinary.Require(axisFlags <= 3 && secondaryAxis < 3 && pointsCount is >= 2 and <= 256 && reserved == 0, "clothing pose axis");
                IasBinary.Require(cursor + (int)pointsCount * PointSize <= raw.Length, "clothing pose points truncated");
                var points = new List<double[]>((int)pointsCount);
                for (var pointIndex = 0; pointIndex < pointsCount; pointIndex++)
                {
                    var point = IasBinary.ReadFloats(raw, cursor + pointIndex * PointSize, 4);
                    IasBinary.Require(point.All(value => double.IsFinite(value) && Math.Abs(value) <= 180), "clothing pose curve points");
                    points.Add(point);
                }
                cursor += (int)pointsCount * PointSize;
                for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
                    IasBinary.Require(points[pointIndex - 1][0] < points[pointIndex][0], "clothing pose curve points");
                axes.Add(new IasClothingPoseAxis((axisFlags & 1) != 0, (axisFlags & 2) != 0, (int)secondaryAxis + 1, points));
            }
            rules.Add(new IasClothingPoseRule((int)source + 1, (int)target + 1, (int)secondary + 1, reference + 1,
                secondaryReference + 1, (int)mode, influence,
                [neutral[1], neutral[2], neutral[3], neutral[0]], [neutral[5], neutral[6], neutral[7], neutral[4]], axes));
        }
        IasBinary.Require(cursor == raw.Length, "clothing pose trailing/truncated bytes");
        foreach (var rule in rules)
        {
            var inputs = new List<int> { rule.Source - 1 };
            if (rule.Reference != 0) inputs.Add(rule.Reference - 1);
            if (rule.Mode != 0)
            {
                inputs.Add(rule.Secondary - 1);
                if (rule.SecondaryReference != 0) inputs.Add(rule.SecondaryReference - 1);
            }
            foreach (var start in inputs)
            {
                for (var index = start; index >= 0; index = bones[index].Parent)
                    IasBinary.Require(!targets.Contains(index), "clothing pose input depends on a driven branch");
            }
        }
        return new IasClothingPoseProgram(1, rules, authorFromRuntime, IasHash.Sha256Hex(raw),
            Convert.ToHexString(fingerprint).ToLowerInvariant(), Convert.ToHexString(raw).ToLowerInvariant());
    }
}
