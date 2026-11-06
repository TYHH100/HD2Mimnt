using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod.Physics;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class Hd2PhysCodecTests
{
    private static Hd2PhysDocument GoldenDocument() => new(0x1101UL, 3,
        [
            new Hd2PhysBone("root", 0, -1, new Vector3(0, 0, 0), [1, 0, 0, 0, 1, 0, 0, 0, 1]),
            new Hd2PhysBone("hair_a", 1, 0, new Vector3(0, 0, 1)),
            new Hd2PhysBone("hair_b", 2, 1, new Vector3(0, 0, 2))
        ],
        [
            new Hd2PhysChain("hair_chain",
            [
                new Hd2PhysJoint("root", 0.02f, Hd2PhysLimitKind.Cone, 0.5f, 0.5f),
                new Hd2PhysJoint("hair_a", 0.02f, Hd2PhysLimitKind.Cone, 0.5f, 0.5f),
                new Hd2PhysJoint("hair_b", 0.02f, Hd2PhysLimitKind.Cone, 0.5f, 0.5f)
            ], 8f, 0.25f, new Vector3(0, 0, -9.81f), Vector3.Zero, 1, uint.MaxValue)
        ], 1f / 60f);

    [Fact]
    public void Write_ReferenceDocument_MatchesPythonProducerByteForByte()
    {
        var golden = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hd2phys-basic-golden.bin"));
        var (bytes, summary) = Hd2PhysCodec.Write(GoldenDocument());

        Assert.Equal(golden, bytes);
        Assert.Equal(692, summary.Bytes);
        Assert.Equal(3, summary.TargetBones);
        Assert.Equal(1, summary.Chains);
        Assert.Equal(3, summary.Joints);
        Assert.Equal(0u, summary.Capabilities);
        Assert.Equal(0x1cfb2094d38c35a2UL, summary.SkeletonFingerprint);
        Assert.Equal(0x1bcbc2c4u, summary.PayloadCrc32);
    }

    [Fact]
    public void Read_Golden_ValidatesHeaderCrcAndLayout()
    {
        var golden = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hd2phys-basic-golden.bin"));
        var inspection = Hd2PhysCodec.Read(golden);

        Assert.Equal(0x1101UL, inspection.UnitId);
        Assert.Equal(0x1cfb2094d38c35a2UL, inspection.SkeletonFingerprint);
        Assert.Equal(3u, inspection.PaletteSlots);
        Assert.Equal(3, inspection.BoneCount);
        Assert.Equal(new[] { "root", "hair_a", "hair_b" }, inspection.JointNames);
        Assert.Equal(new[] { "hair_chain" }, inspection.ChainNames);
        Assert.Equal(0u, inspection.Capabilities);
    }

    [Fact]
    public void Read_CorruptedPayload_RejectsCrcMismatch()
    {
        var golden = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hd2phys-basic-golden.bin"));
        golden[120] ^= 0xFF;
        Assert.Contains("CRC32", Assert.Throws<Hd2PhysFormatException>(() => Hd2PhysCodec.Read(golden)).Message);
    }

    [Fact]
    public void Write_CollisionFilterCapability_AppendsJointFilterTail()
    {
        var document = GoldenDocument() with
        {
            Chains = [GoldenDocument().Chains[0] with
            {
                CollisionMask = 0,
                Joints =
                [
                    new Hd2PhysJoint("root", 0.02f),
                    new Hd2PhysJoint("hair_a", 0.02f, OverrideCollisionFilter: true, CollisionMask: 0),
                    new Hd2PhysJoint("hair_b", 0.02f)
                ]
            }]
        };
        var (bytes, summary) = Hd2PhysCodec.Write(document);
        Assert.NotEqual(0u, summary.Capabilities & (1u << 10));
        Assert.Equal(692 + 3 * 8, bytes.Length);
        Assert.Equal(3, Hd2PhysCodec.Read(bytes).JointCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Write_InvalidBoneCountOrPalette_Rejects(int paletteSlots)
    {
        var document = GoldenDocument() with { PaletteSlots = (uint)paletteSlots };
        Assert.Throws<Hd2PhysFormatException>(() => Hd2PhysCodec.Write(document));
    }

    [Fact]
    public void Write_NonParentChildChain_RejectsInsteadOfGuessing()
    {
        var doc = GoldenDocument() with
        {
            Chains = [new Hd2PhysChain("broken", [new Hd2PhysJoint("root"), new Hd2PhysJoint("hair_b")])]
        };
        Assert.Contains("parent-child", Assert.Throws<Hd2PhysFormatException>(() => Hd2PhysCodec.Write(doc)).Message);
    }

    [Fact]
    public void Write_PreCancelled_Throws()
    {
        Assert.Throws<OperationCanceledException>(() =>
            Hd2PhysCodec.Write(GoldenDocument(), new CancellationToken(true)));
    }
}
