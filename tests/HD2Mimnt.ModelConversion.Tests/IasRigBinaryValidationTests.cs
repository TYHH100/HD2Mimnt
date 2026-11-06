using System.Buffers.Binary;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class IasRigBinaryValidationTests
{
    [Theory]
    [InlineData(8, 0u)]
    [InlineData(8, 2u)]
    [InlineData(12, 0u)]
    [InlineData(12, 31u)]
    [InlineData(12, 33u)]
    [InlineData(12, uint.MaxValue)]
    [InlineData(16, 0u)]
    [InlineData(16, 257u)]
    [InlineData(16, uint.MaxValue)]
    [InlineData(20, 0u)]
    [InlineData(20, 513u)]
    [InlineData(20, uint.MaxValue)]
    [InlineData(28, 1u)]
    [InlineData(28, 2u)]
    [InlineData(28, 4u)]
    [InlineData(28, uint.MaxValue)]
    public void Read_InvalidHeaderOrUnsupportedExtensions_IsRejected(int offset, uint value)
    {
        var data = IasRigTestData.Golden("rig-basic-golden");
        SetU32(data, offset, value);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(24)]
    [InlineData(312)]
    public void Read_CorruptMagicCrcOrPayload_IsRejected(int offset)
    {
        var data = IasRigTestData.Golden("rig-basic-golden");
        data[offset] ^= 0x80;
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData(160, 0u)]
    [InlineData(160, 257u)]
    [InlineData(160, 0x80000000u)]
    [InlineData(160, uint.MaxValue)]
    [InlineData(164, 0u)]
    [InlineData(164, 15u)]
    [InlineData(164, 1048577u)]
    [InlineData(164, 0x80000000u)]
    [InlineData(164, uint.MaxValue)]
    [InlineData(172, 0u)]
    [InlineData(172, 4097u)]
    [InlineData(172, uint.MaxValue)]
    [InlineData(160, 256u)]
    [InlineData(164, 1048576u)]
    public void Read_InvalidOrTruncatedProfileCountsBeforeAllocation_IsRejected(int offset, uint value)
    {
        var data = IasRigTestData.Golden("rig-basic-golden");
        SetU32(data, offset, value);
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData("rig-basic-golden")]
    [InlineData("rig-extended-golden")]
    public void Read_EveryTruncationEvenWithRecomputedCrc_IsRejected(string name)
    {
        var golden = IasRigTestData.Golden(name);
        for (var length = 0; length < golden.Length; length++)
        {
            var truncated = golden[..length];
            if (length >= 32)
                Reseal(truncated);
            Assert.Throws<IasFormatException>(() => IasRigCodec.Read(truncated));
        }
    }

    [Theory]
    [InlineData("rig-basic-golden", 1)]
    [InlineData("rig-basic-golden", 136)]
    [InlineData("rig-extended-golden", 1)]
    [InlineData("rig-extended-golden", 64)]
    public void Read_TrailingGarbageEvenWithValidCrc_IsRejected(string name, int count)
    {
        var data = IasRigTestData.Golden(name).Concat(new byte[count]).ToArray();
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData(32, "empty")]
    [InlineData(180, "empty")]
    [InlineData(32, "unterminated")]
    [InlineData(180, "unterminated")]
    [InlineData(32, "padding")]
    [InlineData(180, "padding")]
    [InlineData(32, "utf8")]
    [InlineData(180, "utf8")]
    [InlineData(32, "utf8-surrogate")]
    [InlineData(180, "utf8-surrogate")]
    public void Read_InvalidNameEncodingTerminationOrPadding_IsRejected(int offset, string kind)
    {
        var data = IasRigTestData.Golden("rig-basic-golden");
        data.AsSpan(offset, 64).Clear();
        switch (kind)
        {
            case "unterminated": data.AsSpan(offset, 64).Fill((byte)'a'); break;
            case "padding": data[offset] = (byte)'a'; data[offset + 63] = 1; break;
            case "utf8": data[offset] = 0xC3; data[offset + 1] = 0x28; break;
            case "utf8-surrogate": data[offset] = 0xED; data[offset + 1] = 0xA0; data[offset + 2] = 0x80; break;
        }
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData(96, 0u)] // Source parent points to itself.
    [InlineData(96, 0xFFFFFFFEu)]
    [InlineData(100, 2u)] // Unknown source flag.
    [InlineData(104, 0xBF800000u)] // Source reflection, -1f.
    [InlineData(104, 0x40000000u)] // Source scale, 2f.
    [InlineData(116, 0x7FC00000u)] // Source translation NaN.
    [InlineData(176, 16u)] // Unknown profile flag.
    [InlineData(176, 12u)] // Conflicting genders.
    [InlineData(176, 4u)] // Gender without header declaration.
    [InlineData(244, 0u)] // Target parent points to itself.
    [InlineData(248, 1u)] // Source index out of range.
    [InlineData(248, 0xFFFFFFFEu)]
    [InlineData(252, 1u)] // Palette index out of range.
    [InlineData(256, 2048u)] // Unknown target flag.
    [InlineData(256, 96u)] // Scene + anchor.
    [InlineData(256, 128u)] // Diagnostic without scene.
    [InlineData(256, 256u)] // Inherit without parent.
    [InlineData(256, 544u)] // Global without source mapping.
    [InlineData(256, 1056u)] // Parent space without source mapping.
    [InlineData(260, 0xBF800000u)] // Negative translation scale.
    [InlineData(260, 0x7F800000u)] // Infinite translation scale.
    [InlineData(264, 0xBF800000u)] // Target reflection.
    [InlineData(276, 0x7FC00000u)] // Target translation NaN.
    public void Read_ValidCrcButInvalidSemanticFields_UsesSharedValidation(int offset, uint value)
    {
        var data = IasRigTestData.Golden("rig-basic-golden");
        SetU32(data, offset, value);
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData("duplicate-units")]
    [InlineData("zero-unit")]
    [InlineData("duplicate-source-names")]
    [InlineData("duplicate-target-names")]
    [InlineData("duplicate-palette")]
    [InlineData("global-rest-mismatch")]
    public void Read_DuplicateTablesOrInvalidGlobalRest_IsRejected(string kind)
    {
        var data = IasRigTestData.Golden("rig-extended-golden");
        switch (kind)
        {
            case "duplicate-units": data.AsSpan(872, 8).CopyTo(data.AsSpan(1856, 8)); break;
            case "zero-unit": data.AsSpan(872, 8).Clear(); break;
            case "duplicate-source-names": data.AsSpan(32, 64).CopyTo(data.AsSpan(152, 64)); break;
            case "duplicate-target-names": data.AsSpan(900, 64).CopyTo(data.AsSpan(1032, 64)); break;
            case "duplicate-palette": SetU32(data, 1032 + 72, 2); break;
            case "global-rest-mismatch": BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(1884 + 84 + 12, 4), 999); break;
        }
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    [Theory]
    [InlineData(28, 0u)] // Neither extension declared.
    [InlineData(28, 1u)] // Missing declared-gender bit.
    [InlineData(28, 2u)] // Grip bytes without extension bit.
    [InlineData(2032, 0u)] // Grip magic.
    [InlineData(2040, 0u)] // An empty grip block is not canonical.
    [InlineData(2040, 1u)] // More records than declared.
    [InlineData(2040, 3u)] // Fewer records than declared.
    [InlineData(2040, 5u)] // Over two grips per profile on average.
    [InlineData(2040, uint.MaxValue)]
    [InlineData(2044, 1u)] // Block reserved field.
    [InlineData(2048, 2u)] // Profile index.
    [InlineData(2048, uint.MaxValue)]
    [InlineData(2052, 7u)] // Hand index.
    [InlineData(2052, uint.MaxValue)]
    [InlineData(2056, 2u)] // Side.
    [InlineData(2060, 1u)] // Record reserved field.
    [InlineData(2064, 0xBF800000u)] // Grip reflection.
    [InlineData(2076, 0x7FC00000u)] // Grip translation NaN.
    [InlineData(2120, 0u)] // Duplicate right side.
    public void Read_InvalidGripExtension_IsRejected(int offset, uint value)
    {
        var data = IasRigTestData.Golden("rig-extended-golden");
        SetU32(data, offset, value);
        Reseal(data);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Read(data));
    }

    private static void SetU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void Reseal(byte[] data) => SetU32(data, 24, Crc32.Compute(data.AsSpan(32)));
}
