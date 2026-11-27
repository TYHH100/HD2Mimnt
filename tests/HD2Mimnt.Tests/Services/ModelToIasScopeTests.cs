using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public sealed class ModelToIasScopeTests
{
    [Fact]
    public void BuildBranches_PreservesParentAndNestedVariantDirectories()
    {
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Model",
            Path = Path.GetTempPath(),
            Manifest = new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Name = "Model",
                Options =
                [
                    new ModOption { Name = "本体", Include = ["本体"] },
                    new ModOption
                    {
                        Name = "上身", Include = ["上身"],
                        SubOptions =
                        [
                            new ModSubOption { Name = "内衣", Include = ["上身/内衣"] },
                            new ModSubOption
                            {
                                Name = "空", Include = ["上身/空"],
                                SubOptions = [new ModSubOption { Name = "隐藏", Include = ["上身/空/隐藏"] }]
                            }
                        ]
                    }
                ]
            }
        };

        var branches = ModelToIasConversionService.BuildBranches(mod);

        Assert.Equal(["本体", "上身", "上身/内衣", "上身/空", "上身/空/隐藏"], branches.Select(branch => branch.Name));
        Assert.Equal(["上身/内衣"], branches[2].Directories);
        Assert.Equal(["上身/空"], branches[3].Directories);
        Assert.Equal(["上身/空/隐藏"], branches[4].Directories);
    }

    [Fact]
    public void BuildBranches_ManifestlessModScansRootAndImmediateDirectories()
    {
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Model", Path = Path.GetTempPath() };

        var branches = ModelToIasConversionService.BuildBranches(mod);

        Assert.Contains(branches, branch => branch.Directories.Single() == ".");
    }
}
