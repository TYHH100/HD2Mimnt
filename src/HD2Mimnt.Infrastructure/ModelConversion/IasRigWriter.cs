using System.Buffers.Binary;
using System.Globalization;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public static class IasRigWriter
{
    private const int HeaderSize = 32;
    private const int SourceSize = 120;
    private const int ProfileSize = 28;
    private const int TargetSize = 132;
    private const int GripSize = 64;

    public static byte[] Write(IReadOnlyList<IasSourceBone> sources, IReadOnlyList<IasRigProfile> profiles)
    {
        IasBinary.Require(sources.Count is >= 1 and <= 256 && profiles.Count is >= 1 and <= 512, "Rig counts");
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            IasBinary.Require(sourceNames.Add(source.Name), "duplicate Rig source name");
            IasBinary.Require(source.Parent >= -1 && source.Parent < index && source.Flags <= 1,
                $"Rig source semantics: {source.Name}, index={index}, parent={source.Parent}, flags={source.Flags}");
            ValidateMatrix(source.RestLocal, "Rig source Rest");
        }

        var payload = new MemoryStream();
        var declaredGender = false;
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var source in sources)
            {
                writer.Write(IasBinary.FixedName(source.Name, 64));
                writer.Write(source.Parent);
                writer.Write(source.Flags);
                WriteMatrix(writer, source.RestLocal);
            }
            var profileIds = new HashSet<ulong>();
            foreach (var profile in profiles)
            {
                var unitId = ulong.Parse(profile.UnitId, NumberStyles.HexNumber);
                IasBinary.Require(profileIds.Add(unitId), "duplicate Rig profile Unit");
                IasBinary.Require(profile.TargetBones.Count is >= 1 and <= 256
                                  && profile.PaletteSlots is >= 1 and <= 4096
                                  && profile.Flags is 0 or 4 or 8
                                  && profile.NeedleHex.Length is >= 32 and <= 2_097_152
                                  && profile.NeedleHex.Length % 2 == 0, "Rig profile schema");
                var needle = Convert.FromHexString(profile.NeedleHex);
                IasBinary.Require(needle.Length is >= 16 and <= 1_048_576, "Rig needle length");
                declaredGender |= profile.Flags != 0;
                writer.Write(unitId);
                writer.Write(profile.TargetBones.Count);
                writer.Write(needle.Length);
                writer.Write(profile.NeedleToPalette);
                writer.Write(profile.PaletteSlots);
                writer.Write(profile.Flags);

                var names = new HashSet<string>(StringComparer.Ordinal);
                var slots = new HashSet<int>();
                for (var index = 0; index < profile.TargetBones.Count; index++)
                {
                    var node = profile.TargetBones[index];
                    IasBinary.Require(names.Add(node.Name), "duplicate Rig target name");
                    IasBinary.Require(slots.Add(node.PaletteSlot), "duplicate Rig palette slot");
                    IasBinary.Require(node.Parent >= -1 && node.Parent < index
                                      && node.SourceIndex >= -1 && node.SourceIndex < sources.Count
                                      && node.PaletteSlot >= 0 && node.PaletteSlot < profile.PaletteSlots
                                      && node.Flags is 1057 or 288, "Rig target indices/flags");
                    IasBinary.Require((node.Flags == 1057 && node.SourceIndex >= 0)
                                      || (node.Flags == 288 && node.Parent >= 0), "Rig target flag semantics");
                    IasBinary.Require(node.TranslationScale is >= 0 and <= 100, "Rig translation scale");
                    ValidateMatrix(node.RestLocal, "Rig target Rest");
                    writer.Write(IasBinary.FixedName(node.Name, 64));
                    writer.Write(node.Parent);
                    writer.Write(node.SourceIndex);
                    writer.Write(node.PaletteSlot);
                    writer.Write(node.Flags);
                    writer.Write((float)node.TranslationScale);
                    WriteMatrix(writer, node.RestLocal);
                }
                writer.Write(needle);
            }

            var grips = profiles.SelectMany((profile, profileIndex) => profile.HandGrips.Select(grip =>
                (ProfileIndex: profileIndex, Profile: profile, Grip: grip))).ToList();
            if (grips.Count > 0)
            {
                writer.Write("HD2GRIP1"u8);
                writer.Write(grips.Count);
                writer.Write(0U);
                foreach (var (profileIndex, profile, grip) in grips)
                {
                    IasBinary.Require(grip.Hand >= 0 && grip.Hand < profile.TargetBones.Count
                                      && grip.Side is 0 or 1, "Rig grip indices");
                    ValidateMatrix(grip.HandToGrip, "Rig grip matrix");
                    writer.Write(profileIndex);
                    writer.Write(grip.Hand);
                    writer.Write(grip.Side);
                    writer.Write(0U);
                    WriteMatrix(writer, grip.HandToGrip);
                }
            }
        }

        var body = payload.ToArray();
        IasBinary.Require(body.LongLength == sources.Count * SourceSize
            + profiles.Sum(profile => ProfileSize + profile.TargetBones.Count * TargetSize + profile.NeedleHex.Length / 2)
            + (profiles.Any(profile => profile.HandGrips.Count > 0)
                ? 16 + profiles.Sum(profile => profile.HandGrips.Count) * GripSize : 0), "Rig payload length");
        var result = new byte[HeaderSize + body.Length];
        "HD2IRG1\0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)sources.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)profiles.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), IasBinary.Crc32(body));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28),
            (profiles.Any(profile => profile.HandGrips.Count > 0) ? 1U : 0U) | (declaredGender ? 2U : 0U));
        body.CopyTo(result, HeaderSize);
        return result;
    }

    private static void WriteMatrix(BinaryWriter writer, IReadOnlyList<double> values)
    {
        foreach (var value in values)
        {
            var single = (float)value;
            IasBinary.Require(float.IsFinite(single), "Rig matrix exceeds float32");
            writer.Write(single);
        }
    }

    private static void ValidateMatrix(IReadOnlyList<double> values, string label)
    {
        IasBinary.Require(values.Count == 12 && values.All(double.IsFinite), label + " schema");
        double[] Column(int index) => [values[index], values[4 + index], values[8 + index]];
        static double Dot(double[] left, double[] right) => left.Zip(right).Sum(pair => pair.First * pair.Second);
        var columns = new[] { Column(0), Column(1), Column(2) };
        IasBinary.Require(columns.All(column => Math.Abs(Math.Sqrt(Dot(column, column)) - 1) <= 0.002), label + " scale");
        IasBinary.Require(Math.Abs(Dot(columns[0], columns[1])) <= 0.002
                          && Math.Abs(Dot(columns[0], columns[2])) <= 0.002
                          && Math.Abs(Dot(columns[1], columns[2])) <= 0.002, label + " orthogonality");
        var determinant = values[0] * (values[5] * values[10] - values[6] * values[9])
            - values[1] * (values[4] * values[10] - values[6] * values[8])
            + values[2] * (values[4] * values[9] - values[5] * values[8]);
        IasBinary.Require(Math.Abs(determinant - 1) <= 0.005, label + " reflection");
    }
}
