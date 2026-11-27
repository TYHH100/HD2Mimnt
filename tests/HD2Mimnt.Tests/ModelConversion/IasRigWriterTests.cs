using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public sealed class IasRigWriterTests
{
    private static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    [Theory]
    [InlineData(0U)]
    [InlineData(8U)]
    public void Write_ReadRoundTripsSourcesProfilesAndGenderExtension(uint profileFlags)
    {
        var sources = new[]
        {
            new IasSourceBone("boss", -1, 1, Identity),
            new IasSourceBone("chest", 0, 0, Identity)
        };
        var targets = new[]
        {
            new IasRigNode("boss", -1, 0, 0, 1057, 1, Identity),
            new IasRigNode("chest", 0, 1, 1, 1057, 1, Identity),
            new IasRigNode("custom", 1, -1, 2, 288, 1, Identity)
        };
        var profile = new IasRigProfile("0000000000000001", targets, 4, profileFlags, new string('0', 32), 0, []);

        var raw = IasRigWriter.Write(sources, [profile]);
        var parsed = IasCompanionReader.ReadRig(raw);

        Assert.Equal(profileFlags == 0 ? 0U : 2U, parsed.Extensions);
        Assert.Equal(sources.Select(source => source.Name), parsed.SourceBones.Select(source => source.Name));
        Assert.Equal(profileFlags, Assert.Single(parsed.Profiles).Flags);
        Assert.Equal(3, Assert.Single(parsed.Profiles).TargetBones.Count);
        Assert.Equal("0000000000000001", Assert.Single(parsed.Profiles).UnitId);
        Assert.Equal(IasHash.Sha256Hex(raw), parsed.Sha256);
    }

    [Fact]
    public void Write_RejectsForwardParentAndReflection()
    {
        var invalidSource = new[]
        {
            new IasSourceBone("boss", -1, 1, Identity),
            new IasSourceBone("chest", 1, 0, Identity)
        };
        var profile = new IasRigProfile("0000000000000001",
            [new IasRigNode("boss", -1, 0, 0, 1057, 1, Identity)], 1, 0, new string('0', 32), 0, []);
        Assert.Throws<InvalidDataException>(() => IasRigWriter.Write(invalidSource, [profile]));

        var reflected = Identity.ToArray();
        reflected[0] = -1;
        var source = new[] { new IasSourceBone("boss", -1, 1, reflected) };
        var target = new[] { new IasRigNode("boss", -1, 0, 0, 1057, 1, reflected) };
        Assert.Throws<InvalidDataException>(() => IasRigWriter.Write(source,
            [profile with { TargetBones = target }]));
    }
}
