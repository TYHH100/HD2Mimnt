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

    [Fact]
    public void ParseFromJson_ObjectOptionsWithoutVersion_TreatedAsV1()
    {
        var manifest = new ManifestParser().ParseFromJson("""
            {"Name":"NoVersionMod","Options":[
              {"Name":"模型","Include":["Model"]},
              {"Name":"材质","Include":["Material"]}]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest.ManifestVersion);
        Assert.Equal(2, manifest.Options!.Count);
        Assert.Equal("Model", manifest.Options[0].Include![0]);
        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            ProfileModOptionResolver.ResolveEffective(manifest, null));
        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Equal(new[] { "Model", "Material" }, plan.Directories);
    }

    [Fact]
    public void ParseFromJson_StringOptionsWithoutVersion_StaysLegacy()
    {
        var manifest = new ManifestParser().ParseFromJson("""
            {"Name":"LegacyMod","Options":["VariantA","VariantB"]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.Legacy, manifest.ManifestVersion);
        Assert.Equal(new[] { "VariantA", "VariantB" }, manifest.LegacyOptions);
        Assert.Null(manifest.Options);
    }

    [Fact]
    public void ParseFromJson_NoOptionsWithoutVersion_StaysLegacy()
    {
        var manifest = new ManifestParser().ParseFromJson("""
            {"Name":"PlainMod"}
            """);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.Legacy, manifest.ManifestVersion);
        Assert.Null(manifest.LegacyOptions);
    }

    private const string ThreeLevelJson = """
        {"Version":1,"Name":"MoeTest","Options":[
          {"Name":"模型","Include":["Model"]},
          {"Name":"材质","SubOptions":[
            {"Name":"丝袜换色","SubOptions":[
              {"Name":"黑丝","Include":["Material/Pantyhose/Black"]}]},
            {"Name":"头发换色","SubOptions":[
              {"Name":"黑","Include":["Material/Hair/Black"]},
              {"Name":"白","Include":["Material/Hair/White"]}]},
            {"Name":"衣服换色","SubOptions":[
              {"Name":"红","Include":["Material/Clothes/Red"]}]}]}]}
        """;

    [Fact]
    public void ParseFromJson_NestedSubOptions_PreservesThirdLevelInclude()
    {
        var manifest = new ManifestParser().ParseFromJson(ThreeLevelJson);

        Assert.NotNull(manifest);
        var material = manifest!.Options![1];
        Assert.Null(material.Include);
        Assert.Equal(3, material.SubOptions!.Count);
        var hair = material.SubOptions[1];
        Assert.Null(hair.Include);
        var leaves = hair.SubOptions!;
        Assert.Equal(2, leaves.Count);
        Assert.Equal("Material/Hair/Black", leaves[0].Include![0]);
        Assert.Equal("Material/Hair/White", leaves[1].Include![0]);
        Assert.Null(material.SubOptions[0].SubOptions![0].SubOptions);
    }

    [Fact]
    public void ResolveDeployPlan_NestedSubOptions_DefaultSelectionCollectsLeafDirectories()
    {
        var manifest = new ManifestParser().ParseFromJson(ThreeLevelJson)!;

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            ProfileModOptionResolver.ResolveEffective(manifest, null));

        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        // 默认：选项全启用，一级子选项取 0，嵌套叶子取 0（黑丝）。
        Assert.Equal(new[] { "Model", "Material/Pantyhose/Black" }, plan.Directories);
    }

    [Fact]
    public void ResolveDeployPlan_NestedSubOptions_SelectedLeafChangesDirectory()
    {
        var manifest = new ManifestParser().ParseFromJson(ThreeLevelJson)!;
        const string stored = """
            {"OptionToggles":[true,true],"OptionSelections":[0,1],"SubOptionSelections":[[],[0,1]]}
            """;

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            ProfileModOptionResolver.ResolveEffective(manifest, stored));

        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Equal(new[] { "Model", "Material/Hair/White" }, plan.Directories);
    }

    [Fact]
    public void ResolveDeployPlan_NestedSubOptionWithoutLeafInclude_FallsBackToFullDirectory()
    {
        var manifest = new ManifestParser().ParseFromJson("""
            {"Version":1,"Name":"BrokenLeaf","Options":[
              {"Name":"材质","SubOptions":[
                {"Name":"头发换色","SubOptions":[{"Name":"黑"},{"Name":"白"}]}]}]}
            """)!;

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            ProfileModOptionResolver.ResolveEffective(manifest, null));

        Assert.Equal(OptionDeployScope.FullDirectory, plan.Scope);
        Assert.Empty(plan.Directories);
    }

    [Fact]
    public void SaveAndParse_NestedSubOptions_RoundTripsThroughPackageFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hd2mimnt_nested_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var parser = new ManifestParser();
            var manifest = parser.ParseFromJson(ThreeLevelJson)!;
            var file = Path.Combine(dir, "manifest.json");
            parser.SaveToFile(manifest, file);

            var reloaded = parser.ParseFromFile(file)!;
            var hair = reloaded.Options![1].SubOptions![1];
            var leaves = hair.SubOptions!;
            Assert.Equal(2, leaves.Count);
            Assert.Equal("Material/Hair/Black", leaves[0].Include![0]);
            Assert.Equal("Material/Hair/White", leaves[1].Include![0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
