using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class Hd2ClOptionScopeTests
{
    [Fact]
    public void Resolve_MaterialBranches_KeepsOptionGroups()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/本体/7805dbf6f83b09ba.patch_0");
            Write(root, "components/0/头发/Black1/9ba626afa44a3aa3.patch_1");
            Write(root, "components/0/头发/Black2/9ba626afa44a3aa3.patch_1");
            Write(root, "components/1/HD2IndependentRig/5c7587f1808f45b595b7670a35f5cc6d.7805dbf6f83b09ba.rigbin");
            var mod = Mod(root, HairGroups());

            var scope = Hd2ClConversionService.ResolveConversionScope(mod, null);

            Assert.NotNull(scope.OptionGroups);
            var group = Assert.Single(scope.OptionGroups!);
            Assert.Equal("头发换色", group.Name);
            Assert.True(group.Enabled);
            Assert.Equal(0, group.DefaultIndex);
            Assert.Equal(["Black1", "Black2"], group.Choices.Select(choice => choice.Name));
            Assert.Equal("components/0/头发/icon.png", group.Image);
            Assert.Equal("components/0/头发/Black1/icon.png", group.Choices[0].Image);
            Assert.Equal(["components/0/本体", "components/1/HD2IndependentRig"], scope.Components);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_BranchWithModelResources_FallsBackToSelectedDirectories()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/本体/7805dbf6f83b09ba.patch_0");
            Write(root, "components/0/头发/Black1/9ba626afa44a3aa3.patch_1");
            Write(root, "components/0/头发/Black2/9ba626afa44a3aa3.patch_1");
            Write(root, "components/0/头发/Black2/1111111111111111.patch_0");
            Write(root, "components/1/HD2IndependentRig/5c7587f1808f45b595b7670a35f5cc6d.7805dbf6f83b09ba.rigbin");
            var mod = Mod(root, HairGroups());

            var scope = Hd2ClConversionService.ResolveConversionScope(mod, null);

            Assert.Null(scope.OptionGroups);
            Assert.Equal(["components/0/头发/Black1", "components/0/本体", "components/1/HD2IndependentRig"], scope.Components);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ModManifest HairGroups() => new()
    {
        Guid = Guid.NewGuid(),
        Name = "配色模组",
        ManifestVersion = ManifestVersion.V1,
        Options =
        [
            new ModOption
            {
                Name = "头发换色",
                Image = "components/0/头发/icon.png",
                SubOptions =
                [
                    new ModSubOption
                    {
                        Name = "Black1",
                        Include = ["components/0/头发/Black1"],
                        Image = "components/0/头发/Black1/icon.png"
                    },
                    new ModSubOption { Name = "Black2", Include = ["components/0/头发/Black2"] }
                ]
            },
            new ModOption { Name = "本体", Include = ["components/0/本体"] },
            new ModOption { Name = "HD2IAS", Include = ["components/1/HD2IndependentRig"] }
        ]
    };

    private static Mod Mod(string root, ModManifest manifest) => new()
    {
        Id = Guid.NewGuid(),
        Name = manifest.Name,
        Path = root,
        Manifest = manifest
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2cl-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
