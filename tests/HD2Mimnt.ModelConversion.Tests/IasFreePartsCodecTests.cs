using System.Text;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class IasFreePartsCodecTests
{
    internal static IasArchiveBinding Binding() => new(0x0123456789abcdef, IasContentDomain.Body,
        [new(0x02030405, [0x101, 0x102, 0x201, 0x301]), new(0x01020304, [0x101, 0x102, 0x201, 0x301])]);

    internal static IasFreePartsDocument Document() => new("hd2free_49d2315949749788", IasContentDomain.Body,
        0x0123456789abcdef, [0x02030405, 0x01020304],
        [new("fp_430a92098311", IasPartSlot.Torso, "上衣",
            [new("fo_e388bf7faa8f", "显示", [new(0x102, 0x1102), new(0x101, 0x1101, true)]),
             new("fo_27792b20481e", "隐藏", [new(0x101, 0x1201), new(0x102, 0x1202)])]),
         new("fp_0b05d3b3664e", IasPartSlot.LeftArm, "袖子",
            [new("fo_cd428ad13605", "长袖", [new(0x201, 0x2101)]),
             new("fo_c8da3052ceff", "短袖", [new(0x201, 0x2201, true)])])]);

    [Fact]
    public void Write_MatchesAqSdkV6GoldenByteForByte()
    {
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "difference-reference.ini"));
        Assert.Equal(expected, IasFreePartsCodec.Write(Document(), Binding()));
    }

    [Fact]
    public void Read_AqSdkV6Golden_PreservesChoicesAndRoutes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "difference-reference.ini"));
        var document = IasFreePartsCodec.Read(bytes, Binding());
        Assert.Equal("hd2free_49d2315949749788", document.ManifestId);
        Assert.Equal("上衣", document.Parts[0].DisplayName);
        Assert.Equal("隐藏", document.Parts[0].Options[1].DisplayName);
        Assert.Equal(new IasUnitRoute(0x101, 0x1101, true), document.Parts[0].Options[0].Routes[0]);
        Assert.Equal(new IasUnitRoute(0x102, 0x1202), document.Parts[0].Options[1].Routes[1]);
        Assert.Equal(bytes, IasFreePartsCodec.Write(document, Binding()));
    }

    [Fact]
    public void Isolation_FirstOptionsAndStaticPart_MatchAqSdkGolden()
    {
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "isolation-reference.ini"));
        Assert.Equal(expected, IasIsolationManifest.Write("owned_fe68097c202119fccb35036f", Document(), Binding(), [new(0x301, 0x3301, true)]));
    }

    [Theory]
    [InlineData("bad,name")]
    [InlineData("bad;name")]
    [InlineData("bad#name")]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    [InlineData(" ")]
    public void Write_UnsafeDisplayName_Rejects(string name)
    {
        var document = Document();
        document = document with { Parts = [document.Parts[0] with { DisplayName = name }, document.Parts[1]] };
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(document, Binding()));
    }

    [Fact]
    public void Write_DisplayNameLength_IsUtf8BytesNotCharacters()
    {
        var document = Document();
        var part = document.Parts[0];
        Assert.NotEmpty(IasFreePartsCodec.Write(document with { Parts = [part with { DisplayName = new string('骨', 42) }] }, Binding()));
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(document with { Parts = [part with { DisplayName = new string('骨', 43) }] }, Binding()));
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(document with { Parts = [part with { DisplayName = "\uD800" }] }, Binding()));
    }

    [Fact]
    public void Write_MissingBodyTypeRoute_RejectsWithoutDroppingIt()
    {
        var doc = Document(); var part = doc.Parts[0];
        var incomplete = part.Options[1] with { Routes = [part.Options[1].Routes[0]] };
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with
        { Parts = [part with { Options = [part.Options[0], incomplete] }] }, Binding()));
    }

    [Fact]
    public void Write_OverlappingPartRoutes_Rejects()
    {
        var doc = Document();
        var other = new IasFreePart("other", IasPartSlot.LeftLeg, "other", [new("off", "off", [new(0x101, 0x9999)])]);
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with { Parts = [doc.Parts[0], other] }, Binding()));
    }

    [Fact]
    public void Write_AliasCollidesWithAnOtherwiseUnusedOriginalUnit_Rejects()
    {
        var doc = Document(); var part = doc.Parts[1];
        var option = part.Options[0] with { Routes = [new(0x201, 0x301)] };
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with
        { Parts = [part with { Options = [option, part.Options[1]] }] }, Binding()));
    }

    [Fact]
    public void Write_DuplicateAliasAcrossOptions_Rejects()
    {
        var doc = Document(); var part = doc.Parts[1];
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with
        { Parts = [part with { Options = [part.Options[0], part.Options[1] with { Routes = part.Options[0].Routes }] }] }, Binding()));
    }

    [Fact]
    public void Write_EquipmentArchiveAndDomainMismatch_Rejects()
    {
        var doc = Document(); var binding = Binding();
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with { ArchiveId = 1 }, binding));
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with { EquipmentKeys = [1] }, binding));
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc with { Domain = IasContentDomain.Helmet }, binding));
        var subset = binding with { Equipment = [binding.Equipment[0] with { NativeUnitIds = [0x101] }, binding.Equipment[1]] };
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc, subset));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Write_RouteCapacity_CountsNativeSlotsNotOptions(int count, bool valid)
    {
        var native = Enumerable.Range(1, count).Select(i => (ulong)i).ToArray();
        var binding = new IasArchiveBinding(1, IasContentDomain.Body, [new(1, native)]);
        var doc = new IasFreePartsDocument("capacity", IasContentDomain.Body, 1, [1],
            [new("part", IasPartSlot.Torso, "part", [new("a", "a", native.Select(id => new IasUnitRoute(id, id + 100)).ToArray()),
                                                   new("b", "b", native.Select(id => new IasUnitRoute(id, id + 200)).ToArray())])]);
        if (valid) Assert.NotEmpty(IasFreePartsCodec.Write(doc, binding));
        else Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Write(doc, binding));
    }

    [Theory]
    [InlineData("manifest_version=6", "manifest_version=5")]
    [InlineData("manifest_version=6", "manifest_version=6\nmanifest_version=6")]
    [InlineData("free_option=fp_430a92098311,fo_27792b20481e,1", "free_option=fp_430a92098311,fo_27792b20481e,2")]
    [InlineData("0x0000000000000101,0x0000000000001101,0x0000000000001201", "0x0000000000000101,0x0000000000009999,0x0000000000001201")]
    [InlineData("difference_logic=free_parts", "difference_logic=free_parts\nunknown_field=1")]
    public void Read_InvalidManifest_Rejects(string original, string replacement)
    {
        var text = Encoding.UTF8.GetString(IasFreePartsCodec.Write(Document(), Binding()));
        Assert.Contains(original, text);
        var bytes = Encoding.UTF8.GetBytes(text.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<IasFormatException>(() => IasFreePartsCodec.Read(bytes, Binding()));
    }

    [Fact]
    public void Isolation_StaticRouteCannotOverwriteFreePartOrReuseItsAlias()
    {
        Assert.Throws<IasFormatException>(() => IasIsolationManifest.Write("owned", Document(), Binding(), [new(0x101, 0x3301)]));
        Assert.Throws<IasFormatException>(() => IasIsolationManifest.Write("owned", Document(), Binding(), [new(0x301, 0x1101)]));
        Assert.Throws<IasFormatException>(() => IasIsolationManifest.Write("owned", Document(), Binding(), [new(0x777, 0x3301)]));
    }

    [Fact]
    public void BodyGroups_BlankAlternativeStillHasExplicitNonzeroUnitRoute()
    {
        var model = SourceModel.Load("fixture.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), SourceFixture.Vtx());
        var group = new IasSourceBodyGroupBinding(0, IasPartSlot.Torso, "hat",
            [new(1, "hidden", [new(0x101, 0x1201)]), new(0, "visible", [new(0x101, 0x1101)])]);
        var document = IasBodyGroupManifestPlanner.Build(model, "fixture", Binding(), [group]);
        Assert.Equal("visible", document.Parts[0].Options[0].Id);
        Assert.Equal("blank", document.Parts[0].Options[1].DisplayName);
        Assert.Equal(0x1201UL, document.Parts[0].Options[1].Routes[0].PublishedUnitId);
        Assert.Throws<IasFormatException>(() => IasBodyGroupManifestPlanner.Build(model, "fixture", Binding(), []));
        Assert.Throws<IasFormatException>(() => IasBodyGroupManifestPlanner.Build(model, "fixture", Binding(),
            [group with { Alternatives = [group.Alternatives[0]] }]));
    }

    [Fact]
    public void BodyGroups_IndependentSwitchesCannotShareOneIasSlot()
    {
        var mdl = SourceFixture.Mdl(); Array.Resize(ref mdl, 1800);
        SourceFixture.Int(mdl, 76, mdl.Length); SourceFixture.Int(mdl, 232, 2); SourceFixture.Int(mdl, 236, 1536);
        for (var index = 0; index < 2; index++)
        {
            var offset = 1536 + index * 16;
            SourceFixture.Int(mdl, offset, 1600 + index * 16 - offset);
            SourceFixture.Int(mdl, offset + 4, 2); SourceFixture.Int(mdl, offset + 8, 1 << index);
            SourceFixture.Int(mdl, offset + 12, 864 - offset); SourceFixture.Text(mdl, 1600 + index * 16, $"switch{index}");
        }
        var vtx = SourceFixture.Vtx(); Array.Resize(ref vtx, 194);
        SourceFixture.Int(vtx, 28, 2); SourceFixture.Int(vtx, 32, 178);
        SourceFixture.Int(vtx, 178, 2); SourceFixture.Int(vtx, 182, 44 - 178);
        SourceFixture.Int(vtx, 186, 2); SourceFixture.Int(vtx, 190, 44 - 186);
        var model = SourceModel.Load("fixture.mdl", mdl, SourceFixture.Vvd(), vtx);
        var groups = new IasSourceBodyGroupBinding[]
        {
            new(0, IasPartSlot.Torso, "first", [new(0, "on", [new(0x101, 0x1101)]), new(1, "off", [new(0x101, 0x1201)])]),
            new(1, IasPartSlot.Torso, "second", [new(0, "on", [new(0x201, 0x2101)]), new(1, "off", [new(0x201, 0x2201)])])
        };
        Assert.Contains("composite alternatives", Assert.Throws<IasFormatException>(() =>
            IasBodyGroupManifestPlanner.Build(model, "fixture", Binding(), groups)).Message);
    }

    [Fact]
    public void ReadWrite_UlongIdsAndHelmetDomain_AreNotTruncated()
    {
        var binding = new IasArchiveBinding(ulong.MaxValue, IasContentDomain.Helmet, [new(uint.MaxValue, [0x8000000000000001])]);
        var doc = new IasFreePartsDocument("helmet", IasContentDomain.Helmet, ulong.MaxValue, [uint.MaxValue],
            [new("head", IasPartSlot.Head, "头部", [new("on", "显示", [new(0x8000000000000001, ulong.MaxValue - 1)])])]);
        var result = IasFreePartsCodec.Read(IasFreePartsCodec.Write(doc, binding), binding);
        Assert.Equal(ulong.MaxValue, result.ArchiveId);
        Assert.Equal(ulong.MaxValue - 1, result.Parts[0].Options[0].Routes[0].PublishedUnitId);
    }

    [Fact]
    public void Cancellation_IsNotReportedAsSuccessfulOutput()
    {
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => IasFreePartsCodec.Write(Document(), Binding(), token));
        Assert.Throws<OperationCanceledException>(() => IasFreePartsCodec.Read([], Binding(), token));
        Assert.Throws<OperationCanceledException>(() => IasIsolationManifest.Write("owned", Document(), Binding(), cancellationToken: token));
    }
}
