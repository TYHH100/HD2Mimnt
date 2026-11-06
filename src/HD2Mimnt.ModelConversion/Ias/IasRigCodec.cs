using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Ias;

/// <summary>Strict little-endian HD2IRG1 protocol codec, including gender and hand-grip extensions.</summary>
public static partial class IasRigCodec
{
    private const int HeaderSize = 32;
    private const int SourceSize = 120;
    private const int ProfileSize = 28;
    // Wire records have no implicit alignment padding.
    private const int TargetSize = 132;
    private const int GripHeaderSize = 16;
    private const int GripSize = 64;
    private const int MaxNeedleSize = 1 << 20;

    public static byte[] Write(IasRigDocument document, CancellationToken cancellationToken = default)
    {
        var layout = Validate(document, cancellationToken);
        var result = new byte[layout.Size];
        var writer = new RigWriter(result);
        writer.Bytes("HD2IRG1\0"u8);
        writer.U32(1);
        writer.U32(HeaderSize);
        writer.U32((uint)document.SourceBones.Count);
        writer.U32((uint)document.Profiles.Count);
        writer.U32(0); // Payload CRC is filled after all records and extensions.
        writer.U32(layout.Extensions);
        foreach (var source in document.SourceBones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Name(source.Name);
            writer.I32(source.Parent);
            writer.U32((uint)source.Flags);
            writer.Matrix(source.RestLocal);
        }
        foreach (var profile in document.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.U64(profile.UnitId);
            writer.U32((uint)profile.TargetBones.Count);
            writer.U32((uint)profile.Needle.Length);
            writer.I32(profile.NeedleToPalette);
            writer.U32(profile.PaletteSlots);
            writer.U32((uint)profile.Flags);
            foreach (var target in profile.TargetBones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Name(target.Name);
                writer.I32(target.Parent);
                writer.I32(target.SourceIndex);
                writer.U32(target.PaletteSlot);
                writer.U32((uint)target.Flags);
                writer.F32(target.TranslationScale);
                writer.Matrix(target.RestLocal);
            }
            writer.Bytes(profile.Needle);
        }
        if (layout.Grips > 0)
        {
            writer.Bytes("HD2GRIP1"u8);
            writer.U32((uint)layout.Grips);
            writer.U32(0);
            for (var i = 0; i < document.Profiles.Count; i++)
                foreach (var grip in document.Profiles[i].HandGrips)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.U32((uint)i);
                    writer.U32(grip.Hand);
                    writer.U32(grip.Side);
                    writer.U32(0);
                    writer.Matrix(grip.HandToGrip);
                }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4),
            Crc32.Compute(result.AsSpan(HeaderSize), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static IasRigDocument Read(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new RigReader(data);
        Require(reader.Take(8).SequenceEqual("HD2IRG1\0"u8), "Invalid HD2IRG1 magic.");
        Require(reader.U32() == 1, "Unsupported HD2IRG1 version.");
        Require(reader.U32() == HeaderSize, "Invalid HD2IRG1 header size.");
        var sourceCount = reader.Count(256, "source bones");
        var profileCount = reader.Count(512, "profiles");
        var crc = reader.U32();
        var extensions = reader.U32();
        Require((extensions & ~3u) == 0, "Unknown HD2IRG1 extensions.");
        Require(reader.Remaining >= sourceCount * SourceSize + profileCount * (ProfileSize + TargetSize + 16),
            "Rig is truncated before its minimum record payload.");
        Require(Crc32.Compute(data[HeaderSize..], cancellationToken) == crc, "Rig payload CRC32 mismatch.");

        var sources = new IasRigSourceBone[sourceCount];
        for (var i = 0; i < sources.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources[i] = new IasRigSourceBone(reader.Name(), reader.I32(), (IasRigSourceFlags)reader.U32(), reader.Matrix());
        }
        var profiles = new IasRigProfile[profileCount];
        for (var i = 0; i < profiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unitId = reader.U64();
            var targetCount = reader.Count(256, "target bones");
            var needleLength = reader.Count(MaxNeedleSize, "needle bytes");
            Require(needleLength >= 16, "Needle must contain at least 16 bytes.");
            var needleToPalette = reader.I32();
            var paletteSlots = reader.U32();
            Require(paletteSlots is >= 1 and <= 4096, "Palette slots must be 1..4096.");
            var flags = (IasRigProfileFlags)reader.U32();
            // Counts are bounded before multiplication or allocation, and lengths are checked by subtraction.
            Require(reader.Remaining >= targetCount * TargetSize + needleLength, "Profile payload is truncated.");
            var targets = new IasRigTargetBone[targetCount];
            for (var j = 0; j < targets.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targets[j] = new IasRigTargetBone(reader.Name(), reader.I32(), reader.I32(), reader.U32(),
                    (IasRigTargetFlags)reader.U32(), reader.F32(), reader.Matrix());
            }
            profiles[i] = new IasRigProfile(unitId, targets, reader.Take(needleLength).ToArray(),
                needleToPalette, paletteSlots, flags);
        }

        if ((extensions & 1) != 0)
        {
            Require(reader.Take(8).SequenceEqual("HD2GRIP1"u8), "Invalid grip extension magic.");
            var count = reader.Count(profileCount * 2, "hand grips");
            Require(reader.U32() == 0, "Grip extension reserved field must be zero.");
            Require(reader.Remaining == count * GripSize, "Grip extension is truncated or contains trailing data.");
            var grips = new List<IasRigHandGrip>[profileCount];
            for (var i = 0; i < grips.Length; i++)
                grips[i] = new List<IasRigHandGrip>(2);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profileIndex = reader.U32();
                Require(profileIndex < profileCount, "Grip profile index is out of range.");
                var hand = reader.U32();
                var side = reader.U32();
                Require(reader.U32() == 0, "Hand grip reserved field must be zero.");
                grips[(int)profileIndex].Add(new IasRigHandGrip(hand, side, reader.Matrix()));
            }
            for (var i = 0; i < profiles.Length; i++)
                profiles[i] = profiles[i] with { HandGrips = grips[i].ToArray() };
        }
        Require(reader.Remaining == 0, "Unexpected trailing rig data.");
        var document = new IasRigDocument(sources, profiles);
        var layout = Validate(document, cancellationToken);
        Require(layout.Extensions == extensions, "Declared extensions do not match profile gender/grip data.");
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private ref struct RigReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public int Remaining => _remaining.Length;

        public ReadOnlySpan<byte> Take(int length)
        {
            Require(length >= 0 && length <= _remaining.Length, "Truncated HD2IRG1 record.");
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public int Count(int maximum, string field)
        {
            var value = U32();
            Require(value >= 1 && value <= maximum, $"Invalid {field} count.");
            return (int)value;
        }

        public string Name()
        {
            var field = Take(64);
            var end = field.IndexOf((byte)0);
            Require(end > 0 && field[(end + 1)..].IndexOfAnyExcept((byte)0) < 0,
                "Bone name must be terminated, nonempty and zero-padded.");
            try
            {
                return StrictUtf8.GetString(field[..end]);
            }
            catch (DecoderFallbackException)
            {
                throw new IasFormatException("Bone name contains invalid UTF-8.");
            }
        }

        public Matrix4x4 Matrix()
        {
            var m = Take(48);
            return new Matrix4x4(
                Single(m, 0), Single(m, 4), Single(m, 8), 0,
                Single(m, 1), Single(m, 5), Single(m, 9), 0,
                Single(m, 2), Single(m, 6), Single(m, 10), 0,
                Single(m, 3), Single(m, 7), Single(m, 11), 1);
        }

        private static float Single(ReadOnlySpan<byte> value, int index) =>
            BinaryPrimitives.ReadSingleLittleEndian(value.Slice(index * 4, 4));
    }

    private ref struct RigWriter(Span<byte> data)
    {
        private Span<byte> _remaining = data;

        private Span<byte> Take(int length)
        {
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }

        public void Bytes(ReadOnlySpan<byte> value) => value.CopyTo(Take(value.Length));
        public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
        public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);
        public void F32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

        public void Name(string value)
        {
            var field = Take(64);
            field.Clear();
            StrictUtf8.GetBytes(value.AsSpan(), field);
        }

        public void Matrix(Matrix4x4 value)
        {
            // System.Numerics uses row vectors. Transpose into wire row-major 3x4, retaining translation.
            F32(value.M11); F32(value.M21); F32(value.M31); F32(value.M41);
            F32(value.M12); F32(value.M22); F32(value.M32); F32(value.M42);
            F32(value.M13); F32(value.M23); F32(value.M33); F32(value.M43);
        }
    }
}
