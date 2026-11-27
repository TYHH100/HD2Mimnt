using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ProfileModOptionCategoryTests
{
    [Fact]
    public void ResolveEffective_OldNestedStateEnablesAllCategoriesAndKeepsLeafSelections()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options =
            [
                new() { Name = "Material", SubOptions =
                [
                    new() { Name = "Hair", SubOptions = [new() { Name = "Black" }, new() { Name = "White" }] },
                    new() { Name = "Socks", SubOptions = [new() { Name = "Black" }, new() { Name = "White" }] }
                ] }
            ]
        };
        var stored = new ProfileModOptionsState
        {
            OptionToggles = [true], OptionSelections = [1], SubOptionSelections = [[1, 0]]
        }.ToJson();

        var state = ProfileModOptionResolver.ResolveEffective(manifest, stored);

        Assert.Equal([true, true], state.SubOptionToggles![0]);
        Assert.Equal([1, 0], state.SubOptionSelections![0]);
        Assert.Equal(0, state.OptionSelections![0]);
    }

    [Fact]
    public void ResolveDeployPlan_NestedCategoriesCollectAllEnabledLeaves()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options =
            [
                new() { Name = "Material", SubOptions =
                [
                    new() { Name = "Hair", SubOptions =
                    [
                        new() { Name = "Black", Include = ["hair/black"] },
                        new() { Name = "White", Include = ["hair/white"] }
                    ] },
                    new() { Name = "Socks", SubOptions =
                    [
                        new() { Name = "Black", Include = ["socks/black"] },
                        new() { Name = "White", Include = ["socks/white"] }
                    ] }
                ] }
            ]
        };
        var state = new ProfileModOptionsState
        {
            OptionToggles = [true], OptionSelections = [1], SubOptionSelections = [[1, 0]],
            SubOptionToggles = [[true, false]]
        };

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest, state);

        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Equal(["hair/white"], plan.Directories);
    }

    [Fact]
    public void ResolveDeployPlan_PlainSubOptionsRemainExclusive()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options =
            [
                new() { Name = "Variant", SubOptions =
                [
                    new() { Name = "A", Include = ["a"] },
                    new() { Name = "B", Include = ["b"] }
                ] }
            ]
        };
        var state = new ProfileModOptionsState
        {
            OptionToggles = [true], OptionSelections = [1], SubOptionToggles = [[true, true]]
        };

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest, state);

        Assert.Equal(["b"], plan.Directories);
    }
}
