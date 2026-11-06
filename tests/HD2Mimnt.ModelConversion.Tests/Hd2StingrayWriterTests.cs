using System.Buffers.Binary;
using System.Numerics;
using HD2Mimnt.ModelConversion.Hd2;

namespace HD2Mimnt.ModelConversion.Tests;

/// <summary>
/// Stingray 写入层的回归：哈希、Unit、补丁 TOC、材质与贴图都要能被读取侧接受。
/// 这些校验对应 <c>PatchResourceInspectionService</c> / <c>ModCompatibilityAnalyzer</c> 的硬约束。
/// </summary>
public class Hd2StingrayWriterTests
{
    [Theory]
    [InlineData("root", 0xBCCF91E5u)]
    [InlineData("Bip01 Spine", 0x0C650FABu)]
    [InlineData("ValveBiped.Bip01_Head1", 0xCCECEF2Cu)]
    [InlineData("g0m0s0x0", 0x1FA762B9u)]
    public void Murmur32_MatchesTheAqsdkReference(string text, uint expected) =>
        Assert.Equal(expected, StingrayHash.Murmur32(text));

    [Fact]
    public void Unit_RoundTripsThroughTheInspectorWithPalettesAndBuffers()
    {
        var definition = SampleDefinition();
        var unit = Hd2UnitBuilder.Build(definition);

        Assert.Empty(Hd2UnitInspector.Validate(unit.Main, unit.Gpu));
        Assert.Equal(Hd2UnitBuilder.UnitVersion, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(0x2C)));
        Assert.Equal((uint)unit.Main.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(0x60)));
        Assert.Equal((ulong)definition.Meshes.Count,
            BinaryPrimitives.ReadUInt64LittleEndian(unit.Main.AsSpan(unit.Main.Length - 8)));
        Assert.Equal((uint)definition.Transforms.Count,
            BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(0x34)))));

        // 顶点缓冲 = 2 顶点 × 40 字节，向上取整到 16 的倍数；索引紧随其后。
        var streamInfo = (int)BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(0x5C));
        var record = streamInfo + (int)BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(streamInfo + 4));
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(record + 0x164)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(record + 0x1A4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(record + 0x188)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(unit.Main.AsSpan(record + 0x1AC)));
        Assert.Equal(128 + 6, unit.Gpu.Length);
        Assert.Equal(1, unit.Summary.TriangleCount);
    }

    [Fact]
    public void Unit_RejectsAMaterialThatNeedsMoreThanTwoHundredFiftySixBones()
    {
        var transforms = Enumerable.Range(0, 300)
            .Select(index => new Hd2UnitTransform($"bone{index}", -1, Matrix4x4.Identity)).ToArray();
        var vertices = Enumerable.Range(0, 300).Select(index => new Hd2UnitVertex(Vector3.Zero, Vector3.UnitY,
            Vector2.Zero, 1f, 0f, 0f, 0f, index, 0, 0, 0)).ToArray();
        var definition = new Hd2UnitDefinition(transforms, [new Hd2UnitMaterial(1u, 2ul)],
            [new Hd2UnitMesh(1u, 0, vertices, [0, 1, 2])]);
        var error = Assert.Throws<Hd2FormatException>(() => Hd2UnitBuilder.Build(definition));
        Assert.Contains("256", error.Message);
    }

    [Fact]
    public void Patch_WritesAReadableTocForEveryResource()
    {
        var unit = Hd2UnitBuilder.Build(SampleDefinition());
        var material = Hd2MaterialWriter.Build(Hd2MaterialFamily.Basic, [
            new Hd2MaterialInput(0x756F6FA6, 0x1111), new Hd2MaterialInput(0xAC652E43, 0x2222),
            new Hd2MaterialInput(0xCAED6CD6, 0x3333)]);
        var texture = Hd2TextureWriter.Build(Hd2TextureWriter.Solid(10, 20, 30), false);
        var bundle = StingrayPatchWriter.Build([
            new StingrayResource(0xAAAA, StingrayTypeIds.Unit, unit.Main, unit.Gpu, ReadOnlyMemory<byte>.Empty),
            new StingrayResource(0xBBBB, StingrayTypeIds.Material, material.Main),
            new StingrayResource(0xCCCC, StingrayTypeIds.Texture, texture.Main, texture.Gpu, ReadOnlyMemory<byte>.Empty)]);

        Assert.Equal(StingrayPatchWriter.Magic, BinaryPrimitives.ReadUInt32LittleEndian(bundle.Patch));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bundle.Patch.AsSpan(4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bundle.Patch.AsSpan(8)));

        var tableEnd = 72 + 32 * 3 + 80 * 3;
        var filesStart = 72 + 32 * 3;
        Assert.Equal(3, bundle.Entries.Count);
        for (var index = 0; index < bundle.Entries.Count; index++)
        {
            var entry = bundle.Entries[index];
            Assert.True(entry.MainOffset >= tableEnd);
            var row = filesStart + index * 80;
            Assert.Equal(entry.FileId, BinaryPrimitives.ReadUInt64LittleEndian(bundle.Patch.AsSpan(row)));
            Assert.Equal(entry.TypeId, BinaryPrimitives.ReadUInt64LittleEndian(bundle.Patch.AsSpan(row + 8)));
            Assert.Equal((ulong)entry.MainOffset, BinaryPrimitives.ReadUInt64LittleEndian(bundle.Patch.AsSpan(row + 16)));
            Assert.Equal((uint)(index + 1), BinaryPrimitives.ReadUInt32LittleEndian(bundle.Patch.AsSpan(row + 76)));
        }
        var unitEntry = bundle.Entries.Single(entry => entry.TypeId == StingrayTypeIds.Unit);
        Assert.Equal((uint)unit.Main.Length, unitEntry.MainSize);
        Assert.Equal((uint)unit.Gpu.Length, unitEntry.GpuSize);
        Assert.True(bundle.Gpu.Length >= unit.Gpu.Length);
        Assert.Empty(bundle.Stream);
    }

    [Fact]
    public void Material_WritesTheBasicTemplateWithReplacedTextureIds()
    {
        var material = Hd2MaterialWriter.Build(Hd2MaterialFamily.Basic, [
            new Hd2MaterialInput(0x756F6FA6, 0x1122334455667788ul),
            new Hd2MaterialInput(0xAC652E43, 0x99AABBCCDDEEFF00ul),
            new Hd2MaterialInput(0xCAED6CD6, 0x0123456789ABCDEFul)]);

        Assert.Empty(Hd2MaterialInspector.Validate(material.Main));
        Assert.Equal(512, material.Main.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(material.Main.AsSpan(0x40)));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(material.Main.AsSpan(0x68)));
        Assert.Equal(484u, BinaryPrimitives.ReadUInt32LittleEndian(material.Main.AsSpan(12)));
        Assert.Equal(0xD51D2A873865D4E5ul, BinaryPrimitives.ReadUInt64LittleEndian(material.Main.AsSpan(24)));
        Assert.Equal(0x756F6FA6u, BinaryPrimitives.ReadUInt32LittleEndian(material.Main.AsSpan(0x88)));
        Assert.Equal(0x1122334455667788ul, BinaryPrimitives.ReadUInt64LittleEndian(material.Main.AsSpan(0x94)));
    }

    [Fact]
    public void Material_AlphaClipUsesItsOwnParentAndSlots()
    {
        var material = Hd2MaterialWriter.Build(Hd2MaterialFamily.AlphaClip, [
            new Hd2MaterialInput(0x1D57DCF3, 1), new Hd2MaterialInput(0xCBDE381B, 2),
            new Hd2MaterialInput(0x848BA63B, 3)]);
        Assert.Empty(Hd2MaterialInspector.Validate(material.Main));
        Assert.Equal(0xD84D04634A1E2F60ul, BinaryPrimitives.ReadUInt64LittleEndian(material.Main.AsSpan(24)));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(material.Main.AsSpan(0x68)));
    }

    [Fact]
    public void Texture_WritesDdsHeaderAndAMipChainPayload()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 3] = 255; }
        var texture = Hd2TextureWriter.Build(new Hd2TextureImage(8, 8, pixels), false);

        Assert.Empty(Hd2TextureInspector.Validate(texture.Main, texture.Gpu));
        Assert.Equal(340, texture.Main.Length);
        Assert.Equal("BC1", texture.Format);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(texture.Main.AsSpan(0x0C0 + 28)));
        Assert.Equal(71u, BinaryPrimitives.ReadUInt32LittleEndian(texture.Main.AsSpan(0x0C0 + 128)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(texture.Main.AsSpan(0x0C0 + 16)));
    }

    private static Hd2UnitDefinition SampleDefinition()
    {
        var transforms = new[]
        {
            new Hd2UnitTransform("root", -1, Matrix4x4.Identity),
            new Hd2UnitTransform("Bip01 Spine", 0, Matrix4x4.CreateTranslation(0, 0, 40))
        };
        var vertices = new[]
        {
            new Hd2UnitVertex(new Vector3(0, 0, 0), Vector3.UnitY, new Vector2(0, 0), 1f, 0f, 0f, 0f, 0, 0, 0, 0),
            new Hd2UnitVertex(new Vector3(10, 0, 10), Vector3.UnitY, new Vector2(0.5f, 0.5f), 0.5f, 0.5f, 0f, 0f, 0, 1, 0, 0),
            new Hd2UnitVertex(new Vector3(0, 0, 20), Vector3.UnitY, new Vector2(0, 1), 0f, 1f, 0f, 0f, 1, 0, 0, 0)
        };
        return new Hd2UnitDefinition(transforms, [new Hd2UnitMaterial(0x1234, 0xABCDEF0123456789ul)],
            [new Hd2UnitMesh(StingrayHash.Murmur32("mesh"), 0, vertices, [0, 1, 2])]);
    }
}
