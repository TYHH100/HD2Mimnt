using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class Hd2ClOptionProductTests
{
    [Fact]
    public void Manifest_FirstOptionMapsRootSoModelPatchStillDeploys()
    {
        using var document = JsonDocument.Parse(Json(Groups()));
        var options = document.RootElement;

        Assert.Equal(3, options.GetArrayLength());
        var root = options[0];
        Assert.Equal("模型", root.GetProperty("Name").GetString());
        Assert.True(root.GetProperty("DefaultEnabled").GetBoolean());
        Assert.Equal(["."], Text(root.GetProperty("Include")));
    }

    [Fact]
    public void Manifest_MaterialGroupsKeepBranchesAndDefaults()
    {
        using var document = JsonDocument.Parse(Json(Groups()));
        var options = document.RootElement;

        var hair = options[1];
        Assert.Equal("Material/1/1", Text(hair.GetProperty("SubOptions")[0].GetProperty("Include")).Single());
        Assert.Equal("Material/1/2", Text(hair.GetProperty("SubOptions")[1].GetProperty("Include")).Single());

        var stockings = options[2];
        Assert.False(stockings.GetProperty("DefaultEnabled").GetBoolean());
        Assert.Equal("Material/2/1", Text(stockings.GetProperty("Include")).Single());
        Assert.False(stockings.TryGetProperty("SubOptions", out _));
    }

    [Fact]
    public void ProductOptionState_ReservesSlotForRootMapping()
    {
        var state = Hd2ClConversionService.ProductOptionState(Groups());

        Assert.Equal([true, true, false], state.OptionToggles);
        Assert.Equal([0, 1, 0], state.OptionSelections);
    }

    [Fact]
    public void ProductOptionState_WithGeneratedManifest_DeploysRootAndSelectedBranch()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options =
            [
                new ModOption { Name = "模型", Include = ["."] },
                new ModOption
                {
                    Name = "头发换色",
                    SubOptions =
                    [
                        new ModSubOption { Name = "Black1", Include = ["Material/1/1"] },
                        new ModSubOption { Name = "Black2", Include = ["Material/1/2"] }
                    ]
                },
                new ModOption { Name = "丝袜换色", Include = ["Material/2/1"] }
            ]
        };

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest,
            Hd2ClConversionService.ProductOptionState(Groups()));

        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        // 根目录映射始终生效；头发组取第 2 个分支；丝袜组在源模组里是关闭的，不参与部署。
        Assert.Equal([".", "Material/1/2"], plan.Directories);
    }

    private static string Json(IReadOnlyList<Hd2ClOptionGroup> groups)
        => IasLua.WriteJson(IasModelPackager.OptionManifest(groups), ensureAscii: false, sortKeys: false, indent: 2);

    private static string[] Text(JsonElement element)
        => element.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();

    private static IReadOnlyList<Hd2ClOptionGroup> Groups() =>
    [
        new Hd2ClOptionGroup("头发换色", null, null, true, 1,
        [
            new Hd2ClOptionChoice("Black1", null, "components/0/头发/Black1"),
            new Hd2ClOptionChoice("Black2", null, "components/0/头发/Black2")
        ]),
        new Hd2ClOptionGroup("丝袜换色", null, null, false, 0,
        [
            new Hd2ClOptionChoice("黑色丝袜", null, "components/0/丝袜换色/黑色丝袜")
        ])
    ];
}
