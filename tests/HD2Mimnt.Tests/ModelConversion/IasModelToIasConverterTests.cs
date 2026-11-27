using System.Buffers.Binary;
using System.Globalization;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public sealed class IasModelToIasConverterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-modeltoias-{Guid.NewGuid():N}");

    public IasModelToIasConverterTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("d65cf7c1fcbe0991")]
    [InlineData("1fc7b7c36f0b1674")]
    [InlineData("001a106b352493d5")]
    public void Catalog_ContainsEquipmentOwnerForUnitsOutsideLegacyPartTable(string unitId)
    {
        Assert.True(IasModelToIasCatalog.TryGet(ulong.Parse(unitId, NumberStyles.HexNumber), out var target));
        Assert.Contains(target.Owners, owner => owner.Archive == "1434ec7cf0edf1bc" && owner.Domain == "body");
    }

    [Fact]
    public void CountRecognizedUnits_ReadsArchiveIdentityWithoutDecodingGeometry()
    {
        var directory = Path.Combine(_root, "model");
        Directory.CreateDirectory(directory);
        var unitId = ulong.Parse("d65cf7c1fcbe0991", NumberStyles.HexNumber);
        var archive = IasArchive.Write(new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Unit, unitId)] = new([1], [], [])
        });
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), archive.Toc);
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0.gpu_resources"), archive.Gpu);
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0.stream"), archive.Stream);

        var count = IasModelToIasConverter.CountRecognizedUnits(_root,
            [new ModelToIasBranch("本体", ["model"])]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountRecognizedUnits_DuplicateMaterialIdentityUsesLastRowAndStillCountsUnits()
    {
        var directory = Path.Combine(_root, "model");
        Directory.CreateDirectory(directory);
        var unitId = ulong.Parse("d65cf7c1fcbe0991", NumberStyles.HexNumber);
        const ulong materialA = 11;
        const ulong materialB = 22;
        var archive = IasArchive.Write(new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Unit, unitId)] = new([1], [], []),
            [(IasResourceTypes.Material, materialA)] = new([2], [], []),
            [(IasResourceTypes.Material, materialB)] = new([3], [], [])
        });
        var rows = IasArchive.ReadRows(archive.Toc);
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(archive.Toc.AsSpan(4));
        var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(archive.Toc.AsSpan(8));
        var rowOffset = IasArchive.HeaderSize + (int)typeCount * IasArchive.TypeRecordSize;
        for (var index = 0; index < rowCount; index++)
        {
            var offset = rowOffset + index * IasArchive.RowSize;
            if (BinaryPrimitives.ReadUInt64LittleEndian(archive.Toc.AsSpan(offset)) != materialB) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(archive.Toc.AsSpan(offset), materialA);
            break;
        }

        Assert.Throws<InvalidDataException>(() => IasArchive.ReadRows(archive.Toc));
        var tolerant = IasArchive.ReadRows(archive.Toc, replaceDuplicates: true);
        Assert.Equal(rows[(IasResourceTypes.Material, materialB)].TocOffset,
            tolerant[(IasResourceTypes.Material, materialA)].TocOffset);
        var entries = IasArchive.ReadEntries(archive.Toc, archive.Gpu, archive.Stream, replaceDuplicates: true);
        Assert.Equal(new byte[] { 3 }, entries[(IasResourceTypes.Material, materialA)].Toc);
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), archive.Toc);
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0.gpu_resources"), archive.Gpu);
        File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0.stream"), archive.Stream);

        var count = IasModelToIasConverter.CountRecognizedUnits(_root,
            [new ModelToIasBranch("本体", ["model"])]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountRecognizedUnits_MalformedPatchDoesNotAbortOtherBranches()
    {
        var valid = Path.Combine(_root, "valid");
        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(valid);
        Directory.CreateDirectory(broken);
        var unitId = ulong.Parse("d65cf7c1fcbe0991", NumberStyles.HexNumber);
        File.WriteAllBytes(Path.Combine(valid, "9ba626afa44a3aa3.patch_0"),
            IasArchive.Write(new Dictionary<(ulong Type, ulong Id), IasResource>
            {
                [(IasResourceTypes.Unit, unitId)] = new([1], [], [])
            }).Toc);
        File.WriteAllBytes(Path.Combine(broken, "9ba626afa44a3aa3.patch_0"), [1, 2, 3]);

        var count = IasModelToIasConverter.CountRecognizedUnits(_root,
            [new ModelToIasBranch("损坏", ["broken"]), new ModelToIasBranch("有效", ["valid"])]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Routes_CombinesIndependentClothingChoicesInsideOneSemanticPart()
    {
        var members = new Dictionary<string, byte[]>
        {
            ["owned_test.isolation.ini"] = System.Text.Encoding.UTF8.GetBytes("""
                isolation_version=1
                isolation_id=owned_test
                domain=body
                archive=0x1111111111111111
                equipment=0x22222222
                unit=0x0000000000000001,0x0000000000000011
                unit=0x0000000000000002,0x0000000000000022
                """),
            ["free_test.difference.ini"] = System.Text.Encoding.UTF8.GetBytes("""
                manifest_version=6
                manifest_id=free_test
                difference_logic=free_parts
                domain=body
                archive=0x1111111111111111
                equipment=0x22222222
                free_part=part_a,Hips,option_both
                free_part_display_name=part_a,胯部（内裤/裙子）
                free_option=part_a,option_both,0
                free_option_display_name=part_a,option_both,内裤+裙子
                free_route=part_a,option_both,0x0000000000000001,0x0000000000000011,0x0000000000000031
                free_route=part_a,option_both,0x0000000000000002,0x0000000000000022,0x0000000000000042
                free_option=part_a,option_panties,1
                free_option_display_name=part_a,option_panties,内裤
                free_route=part_a,option_panties,0x0000000000000001,0x0000000000000011,0x0000000000000031
                free_route=part_a,option_panties,0x0000000000000002,0x0000000000000022,0x0000000000000022
                free_option=part_a,option_skirt,2
                free_option_display_name=part_a,option_skirt,裙子
                free_route=part_a,option_skirt,0x0000000000000001,0x0000000000000011,0x0000000000000011
                free_route=part_a,option_skirt,0x0000000000000002,0x0000000000000022,0x0000000000000042
                """)
        };

        var routes = IasRoutes.Read(members);
        var part = Assert.Single(routes.Parts);

        Assert.Equal("Hips", part.Slot);
        Assert.Equal(3, part.Options.Count);
        Assert.Equal(2, part.Options[0].Routes.Count);
        Assert.Equal("内裤+裙子", part.Options[0].Name);
    }

    [Fact]
    public void CountRecognizedUnits_RejectsPathsOutsideModRoot()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var unitId = ulong.Parse("d65cf7c1fcbe0991", NumberStyles.HexNumber);
        File.WriteAllBytes(Path.Combine(outside, "9ba626afa44a3aa3.patch_0"),
            IasArchive.Write(new Dictionary<(ulong Type, ulong Id), IasResource>
            {
                [(IasResourceTypes.Unit, unitId)] = new([1], [], [])
            }).Toc);

        var count = IasModelToIasConverter.CountRecognizedUnits(_root,
            [new ModelToIasBranch("越界", ["../outside"])]);

        Assert.Equal(0, count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
