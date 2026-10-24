using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Tests;

public class ManifestParserTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("{}")]
    public void ParseFromJson_InvalidOptionalGuid_PreservesMetadataAndOptions(string guid)
    {
        var manifest = new ManifestParser().ParseFromJson($$"""
            {"Version":1,"Guid":{{guid}},"Name":"ValidMod","IconPath":"images/icon.png",
             "Options":[{"Name":"Body","Include":["body"]}]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal("ValidMod", manifest.Name);
        Assert.Equal("images/icon.png", manifest.IconPath);
        Assert.NotEqual(Guid.Empty, manifest.Guid);
        Assert.Equal("body", Assert.Single(manifest.Options!).Include![0]);
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("true", null)]
    [InlineData("2147483648", null)]
    [InlineData("1.5", null)]
    [InlineData("\"invalid\"", null)]
    [InlineData("\"123\"", 123)]
    [InlineData("123", 123)]
    public void ParseFromJson_OptionalNexusId_DoesNotDiscardManifest(string id, int? expected)
    {
        var manifest = new ManifestParser().ParseFromJson($$"""
            {"Version":1,"Name":"ValidMod","NexusData":{"ModId":{{id}}},
             "Options":[{"Name":"Body","Include":["body"]}]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal("ValidMod", manifest.Name);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
        Assert.Equal("body", Assert.Single(manifest.Options!).Include![0]);
        Assert.Equal(expected, manifest.NexusData?.ModId);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(",\"Include\":null", true)]
    [InlineData(",\"Include\":\"body\"", true)]
    [InlineData(",\"Include\":[]", false)]
    public void ParseFromJson_Include_DistinguishesMissingMappingFromExplicitEmpty(string include, bool missing)
    {
        var manifest = new ManifestParser().ParseFromJson($$"""
            {"Version":1,"Name":"IncludeMod","Options":[
              {"Name":"Leaf"{{include}}},
              {"Name":"Group","SubOptions":[{"Name":"Variant"{{include}}}]}]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal(missing, manifest.Options![0].Include == null);
        Assert.Equal(missing, manifest.Options[1].SubOptions![0].Include == null);
        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            ProfileModOptionResolver.ResolveEffective(manifest, null));
        Assert.Equal(missing ? OptionDeployScope.FullDirectory : OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Empty(plan.Directories);
    }
}
