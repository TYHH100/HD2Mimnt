using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class IasRigCodecTests
{
    [Theory]
    [InlineData("rig-basic-golden", 328, "5776ad916cf271487358a417b58be55df63e6bad8c0415454ef73b7fbbe9afb0")]
    [InlineData("rig-extended-golden", 2176, "0f7d5b671c64d689546e717c3cadd0aa26f9f7ccac879bfe0b33543846837e58")]
    public void Write_IndependentPythonInput_MatchesGoldenByteForByte(string name, int size, string sha256)
    {
        var golden = IasRigTestData.Golden(name);
        Assert.Equal(size, golden.Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(golden)).ToLowerInvariant());
        Assert.Equal(golden, IasRigCodec.Write(IasRigTestData.FromJson(name)));
    }

    [Theory]
    [InlineData("rig-basic-golden")]
    [InlineData("rig-extended-golden")]
    public void Read_IndependentPythonBytes_MatchesInputDocument(string name)
    {
        var golden = IasRigTestData.Golden(name);
        var actual = IasRigCodec.Read(golden);
        IasRigTestData.AssertDocument(IasRigTestData.FromJson(name), actual);
        Assert.Equal(golden, IasRigCodec.Write(actual));
    }

    [Fact]
    public void Write_MinimalDocument_UsesExactPackedRecordSizesAndCrc()
    {
        var data = IasRigCodec.Write(IasRigTestData.Basic());
        Assert.Equal(32 + 120 + 28 + 132 + 16, data.Length);
        Assert.Equal("HD2IRG1\0"u8.ToArray(), data[..8]);
        Assert.Equal(1u, U32(data, 8));
        Assert.Equal(32u, U32(data, 12));
        Assert.Equal(1u, U32(data, 16));
        Assert.Equal(1u, U32(data, 20));
        Assert.Equal(0u, U32(data, 28));
        Assert.Equal(Crc32.Compute(data.AsSpan(32)), U32(data, 24));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), data[312..]);
        IasRigTestData.AssertDocument(IasRigTestData.Basic(), IasRigCodec.Read(data));
    }

    [Fact]
    public void Write_RowVectorRotationAndTranslation_TransposesAllTwelveWireElements()
    {
        var matrix = new Matrix4x4(
            0, 1, 0, 0,
            -1, 0, 0, 0,
            0, 0, 1, 0,
            3, 4, 5, 1);
        var document = IasRigTestData.WithSource(IasRigTestData.Basic(), 0, bone => bone with { RestLocal = matrix });
        document = IasRigTestData.WithTarget(document, 0, bone => bone with { RestLocal = matrix });
        var data = IasRigCodec.Write(document);
        float[] wire = [0, -1, 0, 3, 1, 0, 0, 4, 0, 0, 1, 5];
        foreach (var matrixOffset in new[] { 104, 264 })
            for (var i = 0; i < wire.Length; i++)
                Assert.Equal(wire[i], BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(matrixOffset + 4 * i, 4)));
        var read = IasRigCodec.Read(data);
        Assert.Equal(matrix, read.SourceBones[0].RestLocal);
        Assert.Equal(matrix, read.Profiles[0].TargetBones[0].RestLocal);
        Assert.Equal(new Vector3(3, 5, 5), Vector3.Transform(Vector3.UnitX, read.SourceBones[0].RestLocal));
    }

    [Fact]
    public void Write_ArbitraryThreeAxisRotation_RoundTripsWithoutReflectionFalsePositive()
    {
        var matrix = Matrix4x4.CreateRotationX(0.31f) * Matrix4x4.CreateRotationY(-0.73f) *
            Matrix4x4.CreateRotationZ(1.27f) * Matrix4x4.CreateTranslation(12.5f, -6, 0.125f);
        var document = IasRigTestData.WithSource(IasRigTestData.Basic(), 0, bone => bone with { RestLocal = matrix });
        IasRigTestData.AssertDocument(document, IasRigCodec.Read(IasRigCodec.Write(document)));
    }

    [Fact]
    public void Read_GenderAndBothHandGrips_PreservesExtensionDataAndSignedOffsets()
    {
        var data = IasRigTestData.Golden("rig-extended-golden");
        Assert.Equal(3u, U32(data, 28));
        Assert.Equal("HD2GRIP1"u8.ToArray(), data[2032..2040]);
        Assert.Equal(2u, U32(data, 2040));
        var rig = IasRigCodec.Read(data);
        Assert.Equal(IasRigProfileFlags.DynamicPoseMatch | IasRigProfileFlags.GenderMale, rig.Profiles[0].Flags);
        Assert.Equal(IasRigProfileFlags.ObserveOnly | IasRigProfileFlags.GenderFemale, rig.Profiles[1].Flags);
        Assert.Equal(-64, rig.Profiles[0].NeedleToPalette);
        Assert.Equal(int.MinValue, rig.Profiles[1].NeedleToPalette);
        Assert.Equal(ulong.MaxValue, rig.Profiles[1].UnitId);
        Assert.Equal(2, rig.Profiles[0].HandGrips.Count);
        Assert.Empty(rig.Profiles[1].HandGrips);
        Assert.Equal(0u, rig.Profiles[0].HandGrips[0].Side);
        Assert.Equal(1u, rig.Profiles[0].HandGrips[1].Side);
        Assert.Equal(new Vector3(0.125f, -0.25f, 0.5f), rig.Profiles[0].HandGrips[0].HandToGrip.Translation);
    }

    [Theory]
    [InlineData(false, false, 0u)]
    [InlineData(true, false, 1u)]
    [InlineData(false, true, 2u)]
    [InlineData(true, true, 3u)]
    public void Write_OptionalExtensions_DeclaresOnlyPresentData(bool grips, bool gender, uint expected)
    {
        var document = IasRigTestData.FromJson("rig-extended-golden");
        document = document with
        {
            Profiles = document.Profiles.Select(profile => profile with
            {
                Flags = gender ? profile.Flags : profile.Flags & ~(IasRigProfileFlags.GenderMale | IasRigProfileFlags.GenderFemale),
                HandGrips = grips ? profile.HandGrips : Array.Empty<IasRigHandGrip>()
            }).ToArray()
        };
        var data = IasRigCodec.Write(document);
        Assert.Equal(expected, U32(data, 28));
        IasRigTestData.AssertDocument(document, IasRigCodec.Read(data));
    }

    [Fact]
    public void Read_GripsInMultipleProfiles_KeepsPerProfileSidesIndependent()
    {
        var document = IasRigTestData.FromJson("rig-extended-golden");
        document = document with
        {
            Profiles = [document.Profiles[0], document.Profiles[0] with { UnitId = 2 }]
        };
        var read = IasRigCodec.Read(IasRigCodec.Write(document));
        IasRigTestData.AssertDocument(document, read);
        Assert.Equal(2, read.Profiles[1].HandGrips.Count);
        Assert.NotSame(read.Profiles[0].HandGrips, read.Profiles[1].HandGrips);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("profiles")]
    [InlineData("targets")]
    [InlineData("needle")]
    public void Write_MaximumValidCounts_RoundTrips(string field)
    {
        var document = IasRigTestData.Basic();
        document = field switch
        {
            "sources" => document with { SourceBones = Enumerable.Range(0, 256).Select(i =>
                document.SourceBones[0] with { Name = "s" + i, Parent = i - 1 }).ToArray() },
            "profiles" => document with { Profiles = Enumerable.Range(1, 512).Select(i =>
                document.Profiles[0] with { UnitId = (ulong)i }).ToArray() },
            "targets" => IasRigTestData.WithProfile(document, 0, profile => profile with
            {
                PaletteSlots = 4096,
                TargetBones = Enumerable.Range(0, 256).Select(i => profile.TargetBones[0] with
                    { Name = "t" + i, Parent = i - 1, PaletteSlot = (uint)i + 3840 }).ToArray()
            }),
            "needle" => IasRigTestData.WithProfile(document, 0, profile => profile with { Needle = new byte[1 << 20] }),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        IasRigTestData.AssertDocument(document, IasRigCodec.Read(IasRigCodec.Write(document)));
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("unicode")]
    [InlineData("surrogate-pair")]
    public void Write_NameAt63Utf8Bytes_IsAcceptedWithTerminatingZero(string kind)
    {
        var name = kind switch
        {
            "ascii" => new string('a', 63),
            "unicode" => new string('骨', 21),
            _ => string.Concat(Enumerable.Repeat("\U0001D11E", 15)) + "abc"
        };
        var document = IasRigTestData.WithSource(IasRigTestData.Basic(), 0, bone => bone with { Name = name });
        document = IasRigTestData.WithTarget(document, 0, bone => bone with { Name = name });
        var data = IasRigCodec.Write(document);
        Assert.Equal(0, data[32 + 63]);
        Assert.Equal(0, data[180 + 63]);
        IasRigTestData.AssertDocument(document, IasRigCodec.Read(data));
    }

    [Fact]
    public void Write_DuplicateNamesAcrossDifferentTables_AreAllowed()
    {
        var document = IasRigTestData.WithTarget(IasRigTestData.Basic(), 0, bone => bone with { Name = "root" });
        document = document with { Profiles = [document.Profiles[0], document.Profiles[0] with { UnitId = 2 }] };
        IasRigTestData.AssertDocument(document, IasRigCodec.Read(IasRigCodec.Write(document)));
    }

    [Fact]
    public void Read_ReturnedNeedle_DoesNotAliasCallerBuffer()
    {
        var bytes = IasRigCodec.Write(IasRigTestData.Basic());
        var document = IasRigCodec.Read(bytes);
        bytes[312] = 255;
        Assert.Equal(0, document.Profiles[0].Needle[0]);
    }

    [Fact]
    public void ReadAndWrite_CanceledToken_PropagateCancellationEvenForInvalidInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => IasRigCodec.Write(IasRigTestData.Basic(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => IasRigCodec.Write(null!, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => IasRigCodec.Read(Array.Empty<byte>(), cancellation.Token));
        var valid = IasRigCodec.Write(IasRigTestData.Basic());
        Assert.Throws<OperationCanceledException>(() => IasRigCodec.Read(valid, cancellation.Token));
    }

    [Fact]
    public void Write_CancellationAfterValidation_PropagatesInsteadOfReturningPartialBytes()
    {
        using var cancellation = new CancellationTokenSource();
        var document = IasRigTestData.Basic();
        // Validation indexes sources; emission enumerates them. Cancel exactly at that transition.
        document = document with
        {
            SourceBones = new CancelOnEnumerationList<IasRigSourceBone>(document.SourceBones, cancellation.Cancel)
        };
        var error = Assert.Throws<OperationCanceledException>(() => IasRigCodec.Write(document, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed class CancelOnEnumerationList<T>(IReadOnlyList<T> items, Action cancel) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            cancel();
            return items.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
