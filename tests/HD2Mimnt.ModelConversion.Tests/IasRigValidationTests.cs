using System.Numerics;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class IasRigValidationTests
{
    [Theory]
    [InlineData("null-document")]
    [InlineData("null-sources")]
    [InlineData("null-profiles")]
    [InlineData("null-source")]
    [InlineData("null-profile")]
    [InlineData("null-targets")]
    [InlineData("null-target")]
    [InlineData("null-needle")]
    [InlineData("null-grips")]
    [InlineData("empty-sources")]
    [InlineData("too-many-sources")]
    [InlineData("empty-profiles")]
    [InlineData("too-many-profiles")]
    [InlineData("empty-targets")]
    [InlineData("too-many-targets")]
    [InlineData("short-needle")]
    [InlineData("long-needle")]
    [InlineData("zero-palette")]
    [InlineData("large-palette")]
    [InlineData("source-parent-self")]
    [InlineData("source-parent-negative")]
    [InlineData("target-parent-self")]
    [InlineData("target-parent-negative")]
    [InlineData("source-index-negative")]
    [InlineData("source-index-large")]
    [InlineData("palette-outside")]
    [InlineData("palette-unsigned-max")]
    [InlineData("duplicate-source-name")]
    [InlineData("duplicate-target-name")]
    [InlineData("duplicate-palette")]
    [InlineData("zero-unit")]
    [InlineData("duplicate-unit")]
    [InlineData("source-unknown-flags")]
    [InlineData("profile-unknown-flags")]
    [InlineData("profile-conflicting-gender")]
    [InlineData("target-unknown-flags")]
    [InlineData("scale-negative")]
    [InlineData("scale-large")]
    [InlineData("scale-nan")]
    [InlineData("scale-infinity")]
    public void Write_InvalidModel_ThrowsFormatException(string scenario)
    {
        var document = IasRigTestData.Basic();
        var source = document.SourceBones[0];
        var profile = document.Profiles[0];
        var target = profile.TargetBones[0];
        document = scenario switch
        {
            "null-document" => null!,
            "null-sources" => document with { SourceBones = null! },
            "null-profiles" => document with { Profiles = null! },
            "null-source" => document with { SourceBones = [null!] },
            "null-profile" => document with { Profiles = [null!] },
            "null-targets" => P(profile with { TargetBones = null! }),
            "null-target" => P(profile with { TargetBones = [null!] }),
            "null-needle" => P(profile with { Needle = null! }),
            "null-grips" => P(profile with { HandGrips = null! }),
            "empty-sources" => document with { SourceBones = [] },
            "too-many-sources" => document with { SourceBones = Enumerable.Repeat(source, 257).ToArray() },
            "empty-profiles" => document with { Profiles = [] },
            "too-many-profiles" => document with { Profiles = Enumerable.Repeat(profile, 513).ToArray() },
            "empty-targets" => P(profile with { TargetBones = [] }),
            "too-many-targets" => P(profile with { TargetBones = Enumerable.Repeat(target, 257).ToArray() }),
            "short-needle" => P(profile with { Needle = new byte[15] }),
            "long-needle" => P(profile with { Needle = new byte[(1 << 20) + 1] }),
            "zero-palette" => P(profile with { PaletteSlots = 0 }),
            "large-palette" => P(profile with { PaletteSlots = 4097 }),
            "source-parent-self" => S(source with { Parent = 0 }),
            "source-parent-negative" => S(source with { Parent = -2 }),
            "target-parent-self" => T(target with { Parent = 0 }),
            "target-parent-negative" => T(target with { Parent = -2 }),
            "source-index-negative" => T(target with { SourceIndex = -2 }),
            "source-index-large" => T(target with { SourceIndex = 1 }),
            "palette-outside" => T(target with { PaletteSlot = 1 }),
            "palette-unsigned-max" => T(target with { PaletteSlot = uint.MaxValue }),
            "duplicate-source-name" => document with { SourceBones = [source, source] },
            "duplicate-target-name" => P(profile with { PaletteSlots = 2, TargetBones = [target, target with { PaletteSlot = 1 }] }),
            "duplicate-palette" => P(profile with { TargetBones = [target, target with { Name = "other" }] }),
            "zero-unit" => P(profile with { UnitId = 0 }),
            "duplicate-unit" => document with { Profiles = [profile, profile] },
            "source-unknown-flags" => S(source with { Flags = (IasRigSourceFlags)2 }),
            "profile-unknown-flags" => P(profile with { Flags = (IasRigProfileFlags)16 }),
            "profile-conflicting-gender" => P(profile with { Flags = IasRigProfileFlags.GenderMale | IasRigProfileFlags.GenderFemale }),
            "target-unknown-flags" => T(target with { Flags = (IasRigTargetFlags)2048 }),
            "scale-negative" => T(target with { TranslationScale = -0.001f }),
            "scale-large" => T(target with { TranslationScale = 100.001f }),
            "scale-nan" => T(target with { TranslationScale = float.NaN }),
            "scale-infinity" => T(target with { TranslationScale = float.PositiveInfinity }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(document));
        return;

        IasRigDocument P(IasRigProfile value) => document with { Profiles = [value] };
        IasRigDocument S(IasRigSourceBone value) => document with { SourceBones = [value] };
        IasRigDocument T(IasRigTargetBone value) => P(profile with { TargetBones = [value] });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("nul")]
    [InlineData("ascii64")]
    [InlineData("unicode64")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    public void Write_InvalidSourceOrTargetName_IsRejected(string kind)
    {
        var name = kind switch
        {
            "empty" => "",
            "null" => null!,
            "nul" => "a\0b",
            "ascii64" => new string('a', 64),
            "unicode64" => new string('骨', 21) + "a",
            "high-surrogate" => "bad\uD800",
            "low-surrogate" => "\uDC00bad",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(
            IasRigTestData.WithSource(IasRigTestData.Basic(), 0, bone => bone with { Name = name })));
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(
            IasRigTestData.WithTarget(IasRigTestData.Basic(), 0, bone => bone with { Name = name })));
    }

    [Theory]
    [InlineData("reflection")]
    [InlineData("scale")]
    [InlineData("shear")]
    [InlineData("determinant")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative-infinity")]
    [InlineData("hidden-nan")]
    [InlineData("perspective-x")]
    [InlineData("perspective-y")]
    [InlineData("perspective-z")]
    [InlineData("homogeneous")]
    public void Write_InvalidMatrixInSourceTargetOrGrip_IsRejected(string kind)
    {
        var matrix = Matrix4x4.Identity;
        switch (kind)
        {
            case "reflection": matrix.M11 = -1; break;
            case "scale": matrix.M11 = 1.01f; break;
            case "shear": matrix.M21 = 0.003f; break;
            case "determinant": matrix.M11 = matrix.M22 = matrix.M33 = 1.0019f; break;
            case "nan": matrix.M41 = float.NaN; break;
            case "infinity": matrix.M22 = float.PositiveInfinity; break;
            case "negative-infinity": matrix.M43 = float.NegativeInfinity; break;
            case "hidden-nan": matrix.M14 = float.NaN; break;
            case "perspective-x": matrix.M14 = 0.001f; break;
            case "perspective-y": matrix.M24 = 0.001f; break;
            case "perspective-z": matrix.M34 = 0.001f; break;
            case "homogeneous": matrix.M44 = 0; break;
        }
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(
            IasRigTestData.WithSource(IasRigTestData.Basic(), 0, bone => bone with { RestLocal = matrix })));
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(
            IasRigTestData.WithTarget(IasRigTestData.Basic(), 0, bone => bone with { RestLocal = matrix })));
        var rig = IasRigTestData.FromJson("rig-extended-golden");
        rig = IasRigTestData.WithProfile(rig, 0, profile => profile with
        {
            HandGrips = [profile.HandGrips[0] with { HandToGrip = matrix }]
        });
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
    }

    [Theory]
    [InlineData(IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.AnchorOnly, 0)]
    [InlineData(IasRigTargetFlags.DiagnosticMotion, 0)]
    [InlineData(IasRigTargetFlags.InheritParentPose, 0)]
    [InlineData(IasRigTargetFlags.SourceParentSpace, 0)]
    [InlineData(IasRigTargetFlags.SourceParentSpace | IasRigTargetFlags.SceneGraphIndex, -1)]
    [InlineData(IasRigTargetFlags.SourceParentSpace | IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.SourceGlobalPose, 0)]
    [InlineData(IasRigTargetFlags.SourceGlobalPose, 0)]
    [InlineData(IasRigTargetFlags.SourceGlobalPose | IasRigTargetFlags.SceneGraphIndex, -1)]
    public void Write_InvalidTargetFlagCombinations_AreRejected(IasRigTargetFlags flags, int sourceIndex)
    {
        var rig = IasRigTestData.WithTarget(IasRigTestData.Basic(), 0,
            bone => bone with { SourceIndex = sourceIndex, Flags = flags });
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
    }

    [Theory]
    [InlineData("unmapped-parent")]
    [InlineData("parent-space-inherit")]
    [InlineData("global-inherit")]
    public void Write_InvalidFlagCombinationsWithExistingParent_AreRejected(string kind)
    {
        var rig = IasRigTestData.Basic();
        var root = rig.Profiles[0].TargetBones[0] with { SourceIndex = kind == "unmapped-parent" ? -1 : 0 };
        var flags = IasRigTargetFlags.SceneGraphIndex | (kind == "global-inherit"
            ? IasRigTargetFlags.SourceGlobalPose : IasRigTargetFlags.SourceParentSpace);
        if (kind != "unmapped-parent")
            flags |= IasRigTargetFlags.InheritParentPose;
        var child = root with { Name = "child", Parent = 0, SourceIndex = 0, PaletteSlot = 1, Flags = flags };
        rig = IasRigTestData.WithProfile(rig, 0, profile => profile with { PaletteSlots = 2, TargetBones = [root, child] });
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
    }

    [Theory]
    [InlineData(IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.DiagnosticMotion)]
    [InlineData(IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.SourceParentSpace)]
    [InlineData(IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.SourceGlobalPose)]
    [InlineData(IasRigTargetFlags.AnchorOnly | IasRigTargetFlags.Attachment | IasRigTargetFlags.Backpack)]
    public void Write_ValidTargetFlagCombinations_RoundTrip(IasRigTargetFlags flags)
    {
        var rig = IasRigTestData.WithTarget(IasRigTestData.Basic(), 0, bone => bone with { SourceIndex = 0, Flags = flags });
        IasRigTestData.AssertDocument(rig, IasRigCodec.Read(IasRigCodec.Write(rig)));
    }

    [Fact]
    public void Write_InheritParentPoseWithParentAndNoSource_IsValid()
    {
        var rig = IasRigTestData.Basic();
        var root = rig.Profiles[0].TargetBones[0];
        rig = IasRigTestData.WithProfile(rig, 0, profile => profile with
        {
            PaletteSlots = 2,
            TargetBones = [root, root with { Name = "child", Parent = 0, PaletteSlot = 1, Flags = IasRigTargetFlags.InheritParentPose }]
        });
        IasRigTestData.AssertDocument(rig, IasRigCodec.Read(IasRigCodec.Write(rig)));
    }

    [Theory]
    [InlineData(0.00019f, true)]
    [InlineData(0.00021f, false)]
    public void Write_SourceGlobalRest_EnforcesAbsoluteTolerance(float difference, bool valid)
    {
        var rig = IasRigTestData.WithTarget(IasRigTestData.Basic(), 0, bone => bone with
        {
            SourceIndex = 0,
            Flags = IasRigTargetFlags.SceneGraphIndex | IasRigTargetFlags.SourceGlobalPose,
            RestLocal = Matrix4x4.CreateTranslation(difference, 0, 0)
        });
        if (valid)
            IasRigTestData.AssertDocument(rig, IasRigCodec.Read(IasRigCodec.Write(rig)));
        else
            Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
    }

    [Fact]
    public void Write_GlobalRestUsesLocalTimesParent_NotParentTimesLocal()
    {
        var rig = IasRigTestData.FromJson("rig-extended-golden");
        var root = rig.SourceBones[0].RestLocal;
        var local = rig.SourceBones[1].RestLocal;
        var target = rig.Profiles[1].TargetBones[0] with { SourceIndex = 1, RestLocal = local * root };
        rig = IasRigTestData.WithProfile(rig, 1, profile => profile with { TargetBones = [target] });
        IasRigTestData.AssertDocument(rig, IasRigCodec.Read(IasRigCodec.Write(rig)));
        rig = IasRigTestData.WithTarget(rig, 0, bone => bone with { RestLocal = root * local }, 1);
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
    }

    [Theory]
    [InlineData("duplicate-side")]
    [InlineData("side-outside")]
    [InlineData("hand-outside")]
    [InlineData("wrong-hand")]
    [InlineData("wrong-side")]
    [InlineData("missing-source")]
    [InlineData("hand-no-parent-space")]
    [InlineData("elbow-no-parent-space")]
    [InlineData("shoulder-no-parent-space")]
    [InlineData("broken-ancestry")]
    [InlineData("wrong-source-name")]
    [InlineData("null-grip")]
    [InlineData("too-many-grips")]
    public void Write_InvalidHandGripMappingOrChain_IsRejected(string kind)
    {
        var rig = IasRigTestData.FromJson("rig-extended-golden");
        var first = rig.Profiles[0].HandGrips[0];
        switch (kind)
        {
            case "duplicate-side": Grips([first, first]); break;
            case "side-outside": Grips([first with { Side = uint.MaxValue }]); break;
            case "hand-outside": Grips([first with { Hand = uint.MaxValue }]); break;
            case "wrong-hand": Grips([first with { Hand = 2 }]); break;
            case "wrong-side": Grips([first with { Side = 1 }]); break;
            case "missing-source":
                rig = IasRigTestData.WithTarget(rig, 3, bone => bone with { SourceIndex = -1, Flags = IasRigTargetFlags.SceneGraphIndex });
                break;
            case "hand-no-parent-space": RemoveParentSpace(3); break;
            case "elbow-no-parent-space": RemoveParentSpace(2); break;
            case "shoulder-no-parent-space": RemoveParentSpace(1); break;
            case "broken-ancestry": rig = IasRigTestData.WithTarget(rig, 2, bone => bone with { Parent = -1 }); break;
            case "wrong-source-name": rig = IasRigTestData.WithSource(rig, 3, bone => bone with { Name = "not_r_hand" }); break;
            case "null-grip": Grips([null!]); break;
            case "too-many-grips": Grips([first, first, first]); break;
        }
        Assert.Throws<IasFormatException>(() => IasRigCodec.Write(rig));
        return;

        void Grips(IReadOnlyList<IasRigHandGrip> values) =>
            rig = IasRigTestData.WithProfile(rig, 0, profile => profile with { HandGrips = values });
        void RemoveParentSpace(int index) => rig = IasRigTestData.WithTarget(rig, index,
            bone => bone with { Flags = bone.Flags & ~IasRigTargetFlags.SourceParentSpace });
    }
}
