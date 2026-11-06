using System.Buffers.Binary;
using System.Security.Cryptography;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class IasCompanionBuilderTests
{
    private static IasArchiveBinding Binding() => new(0xabcdef, IasContentDomain.Body, [new(1, [0x101, 0x201])]);
    private static IasFreePartsDocument Document(bool rig) => new("difference", IasContentDomain.Body, 0xabcdef, [1],
        [new("torso", IasPartSlot.Torso, "上衣", [new("on", "显示", [new(0x101, 0x1101, rig)]),
                                              new("off", "隐藏", [new(0x101, 0x1201)])])]);

    [Fact]
    public void Build_ValidRigAndRoutes_ProducesConsistentCompanionFiles()
    {
        var rig = MinimalRig(0x1101);
        var files = IasCompanionBuilder.Build("project", "owned", Document(true), Binding(), rig);
        Assert.Equal(3, files.Count);
        Assert.All(files, file => Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file.Data.Span)), file.Sha256));
        var difference = Assert.Single(files.Where(file => file.Name.EndsWith(".difference.ini", StringComparison.Ordinal)));
        Assert.True(IasFreePartsCodec.Read(difference.Data.Span, Binding()).Parts[0].Options[0].Routes[0].HasRigProfile);
        var rigFile = Assert.Single(files.Where(file => file.Name.EndsWith(".rigbin", StringComparison.Ordinal)));
        Assert.Equal("project.0000000000abcdef.rigbin", rigFile.Name);
        Assert.Equal(0x1101UL, Assert.Single(IasRigCodec.Read(rigFile.Data.Span).Profiles).UnitId);
    }

    [Fact]
    public void Build_AbsentOrUnreferencedRig_RejectsInsteadOfPublishingIncompleteFiles()
    {
        Assert.Throws<IasFormatException>(() => IasCompanionBuilder.Build("project", "owned", Document(true), Binding()));
        Assert.Throws<IasFormatException>(() => IasCompanionBuilder.Build("project", "owned", Document(true), Binding(), MinimalRig(0x9999)));
        Assert.Throws<IasFormatException>(() => IasCompanionBuilder.Build("project", "owned", Document(false), Binding(), MinimalRig(0x1101)));
    }

    [Fact]
    public void Build_NoRigRequired_DoesNotInventAnEmptyRigFile()
    {
        var files = IasCompanionBuilder.Build("project", "owned", Document(false), Binding());
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, file => file.Name.EndsWith(".rigbin", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UnsafeNameOrCancelled_DoesNotReturnFiles()
    {
        Assert.Throws<IasFormatException>(() => IasCompanionBuilder.Build("../project", "owned", Document(false), Binding()));
        Assert.Throws<OperationCanceledException>(() => IasCompanionBuilder.Build("project", "owned", Document(false), Binding(),
            cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Build_DifferentArchives_DoNotShareOutputNames()
    {
        var first = IasCompanionBuilder.Build("same-project", "owned", Document(false), Binding());
        var other = Document(false) with { ArchiveId = 0x123456 };
        var second = IasCompanionBuilder.Build("same-project", "owned", other, Binding() with { ArchiveId = 0x123456 });
        Assert.Empty(first.Select(file => file.Name).Intersect(second.Select(file => file.Name)));
    }

    private static IasRigDocument MinimalRig(ulong unit)
    {
        var data = new byte[328];
        SourceFixture.Text(data, 0, "HD2IRG1\0");
        SourceFixture.Int(data, 8, 1); SourceFixture.Int(data, 12, 32); SourceFixture.Int(data, 16, 1); SourceFixture.Int(data, 20, 1);
        SourceFixture.Text(data, 32, "hips"); SourceFixture.Int(data, 96, -1);
        foreach (var offset in new[] { 104, 124, 144 }) SourceFixture.Float(data, offset, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(152), unit);
        SourceFixture.Int(data, 160, 1); SourceFixture.Int(data, 164, 16); SourceFixture.Int(data, 172, 1);
        SourceFixture.Text(data, 180, "hips"); SourceFixture.Int(data, 244, -1); SourceFixture.Float(data, 260, 1);
        foreach (var offset in new[] { 264, 284, 304 }) SourceFixture.Float(data, offset, 1);
        for (var i = 312; i < data.Length; i++) data[i] = (byte)(i - 311);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), Crc32.Compute(data.AsSpan(32)));
        return IasRigCodec.Read(data);
    }
}
