using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class IasClothingPoseTests
{
    private static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    [Fact]
    public void Read_AcceptsFloat32Fingerprint()
    {
        var profile = Profile();

        var program = IasClothingPose.Read(Pose(profile, Fingerprint(profile)), profile);

        Assert.Single(program.Rules);
        Assert.Equal("00000000000000aa", profile.UnitId);
    }

    [Fact]
    public void Read_RejectsFingerprintBuiltFromDoubleWidths()
    {
        var profile = Profile();
        var payload = Payload(profile, restWidth: 8);

        var raw = Pose(profile, SHA256.HashData(payload));

        var error = Assert.Throws<InvalidDataException>(() => IasClothingPose.Read(raw, profile));
        Assert.Contains("fingerprint differs", error.Message);
    }

    private static IasRigProfile Profile()
    {
        var bones = new List<IasRigNode>
        {
            new("root", -1, 0, 0, 288, 1.0, Identity),
            new("child", 0, 1, 1, 288, 1.5, Identity)
        };
        return new IasRigProfile("00000000000000aa", bones, 2, 0, string.Empty, 0, []);
    }

    private static byte[] Fingerprint(IasRigProfile profile) => SHA256.HashData(Payload(profile, restWidth: 4));

    /// <summary>按上游 TARGET 布局（scale f32、rest 12×f32）重算指纹载荷。</summary>
    private static byte[] Payload(IasRigProfile profile, int restWidth)
    {
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(ulong.Parse(profile.UnitId, System.Globalization.NumberStyles.HexNumber)));
        payload.AddRange(BitConverter.GetBytes((uint)profile.PaletteSlots));
        payload.AddRange(BitConverter.GetBytes(profile.Flags));
        payload.AddRange(BitConverter.GetBytes((uint)profile.TargetBones.Count));
        foreach (var bone in profile.TargetBones)
        {
            payload.AddRange(IasBinary.FixedName(bone.Name, 64));
            payload.AddRange(BitConverter.GetBytes(bone.Parent));
            payload.AddRange(BitConverter.GetBytes(bone.SourceIndex));
            payload.AddRange(BitConverter.GetBytes((uint)bone.PaletteSlot));
            payload.AddRange(BitConverter.GetBytes(bone.Flags));
            payload.AddRange(restWidth == 4
                ? BitConverter.GetBytes((float)bone.TranslationScale)
                : BitConverter.GetBytes(bone.TranslationScale));
            foreach (var value in bone.RestLocal)
                payload.AddRange(restWidth == 4 ? BitConverter.GetBytes((float)value) : BitConverter.GetBytes(value));
        }
        return payload.ToArray();
    }

    private static byte[] Pose(IasRigProfile profile, byte[] fingerprint)
    {
        var tail = new List<byte>();
        tail.AddRange(BitConverter.GetBytes(0U));
        tail.AddRange(BitConverter.GetBytes(1U));
        tail.AddRange(BitConverter.GetBytes(0U));
        tail.AddRange(BitConverter.GetBytes(-1));
        tail.AddRange(BitConverter.GetBytes(-1));
        tail.AddRange(BitConverter.GetBytes(0U));
        tail.AddRange(BitConverter.GetBytes(1.0f));
        tail.AddRange(BitConverter.GetBytes(0f));
        tail.AddRange(BitConverter.GetBytes(0f));
        tail.AddRange(BitConverter.GetBytes(0f));
        tail.AddRange(BitConverter.GetBytes(1f));
        tail.AddRange(BitConverter.GetBytes(1f));
        tail.AddRange(BitConverter.GetBytes(0f));
        tail.AddRange(BitConverter.GetBytes(0f));
        tail.AddRange(BitConverter.GetBytes(0f));
        for (var axis = 0; axis < 3; axis++)
        {
            tail.AddRange(BitConverter.GetBytes(0U));
            tail.AddRange(BitConverter.GetBytes(0U));
            tail.AddRange(BitConverter.GetBytes(2U));
            tail.AddRange(BitConverter.GetBytes(0U));
            foreach (var point in new[] { (0f, 0f, 0f, 1f), (90f, 0f, 0f, 1f) })
            {
                tail.AddRange(BitConverter.GetBytes(point.Item1));
                tail.AddRange(BitConverter.GetBytes(point.Item2));
                tail.AddRange(BitConverter.GetBytes(point.Item3));
                tail.AddRange(BitConverter.GetBytes(point.Item4));
            }
        }
        var raw = new byte[120];
        "HD2POSE1"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), 120);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), (uint)(120 + tail.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(20), IasBinary.Crc32(tail.ToArray()));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32),
            ulong.Parse(profile.UnitId, System.Globalization.NumberStyles.HexNumber));
        fingerprint.CopyTo(raw, 40);
        for (var index = 0; index < 12; index++)
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(72 + 4 * index), (float)Identity[index]);
        return raw.Concat(tail).ToArray();
    }
}
