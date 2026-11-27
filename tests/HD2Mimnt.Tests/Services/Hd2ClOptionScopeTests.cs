using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.ModelConversion;
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

    [Fact]
    public void Resolve_IndependentColorSwitchesWithSharedModels_BecomesExclusiveMaterialGroup()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/HD2IndependentRig/5c7587f1808f45b595b7670a35f5cc6d.98da89d0d5132867.rigbin");
            Write(root, "components/1/本体/98da89d0d5132867.patch_0");
            Write(root, "components/1/配套/01/98da89d0d5132867.patch_1");
            Write(root, "components/1/配套/02/9102938b4b2aef9d.patch_1");
            Write(root, "components/1/配套/03/98da89d0d5132867.patch_1");
            var options = new List<ModOption>();
            string[] names = ["默认白蓝", "白红", "白紫", "白粉", "黑蓝", "黑红", "纯黑"];
            for (var index = 0; index < names.Length; index++)
            {
                var color = "components/1/配色/" + names[index];
                Write(root, color + "/9ba626afa44a3aa3.patch_" + (40 + index));
                options.Add(new ModOption
                {
                    Name = names[index],
                    Image = "components/1/封面/" + names[index] + ".png",
                    Include = ["components/1/本体", index < 4 ? "components/1/配套/01" : "components/1/配套/03",
                        "components/1/配套/02", color]
                });
            }
            var mod = Mod(root, new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Name = "FS-23",
                Options = [new ModOption
                {
                    Name = "HD2IAS",
                    Include = ["components/0/HD2IndependentRig"]
                }, .. options]
            });

            var scope = Hd2ClConversionService.ResolveConversionScope(mod, null);

            var group = Assert.Single(scope.OptionGroups!);
            Assert.Equal("材质配色", group.Name);
            Assert.True(group.Enabled);
            Assert.Equal(6, group.DefaultIndex);
            Assert.Equal(options.Select(option => option.Name), group.Choices.Select(choice => choice.Name));
            Assert.Equal(options.Select(option => option.Image), group.Choices.Select(choice => choice.Image));
            Assert.Equal(["components/0/HD2IndependentRig", "components/1/本体", "components/1/配套/01",
                "components/1/配套/02", "components/1/配套/03"], scope.Components);
            Assert.DoesNotContain(scope.Components!, directory => directory.StartsWith("components/1/配色/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_IndependentColorSwitches_PreservesSavedChoiceAndExcludesOtherMaterials()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/HD2IndependentRig/5c7587f1808f45b595b7670a35f5cc6d.98da89d0d5132867.rigbin");
            Write(root, "components/1/本体/98da89d0d5132867.patch_0");
            Write(root, "components/1/配套/01/98da89d0d5132867.patch_1");
            var options = new List<ModOption>();
            for (var index = 0; index < 3; index++)
            {
                var color = "components/1/配色/" + (index + 1);
                Write(root, color + "/9ba626afa44a3aa3.patch_" + (40 + index));
                options.Add(new ModOption
                {
                    Name = "配色" + (index + 1),
                    Include = ["components/1/本体", "components/1/配套/01", color]
                });
            }
            var manifest = new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Name = "配色模组",
                Options = [new ModOption
                {
                    Name = "HD2IAS",
                    Include = ["components/0/HD2IndependentRig"]
                }, .. options]
            };
            var state = new ProfileModOptionsState { OptionToggles = [true, false, true, false] };

            var scope = Hd2ClConversionService.ResolveConversionScope(Mod(root, manifest),
                JsonSerializer.Serialize(state));
            var productManifest = new ManifestParser().ParseFromJson(
                IasLua.WriteJson(IasValue.Obj(("Version", IasValue.Of(1)),
                    ("Options", IasModelPackager.OptionManifest(scope.OptionGroups!))), ensureAscii: false,
                    sortKeys: false, indent: 2))!;

            var group = Assert.Single(scope.OptionGroups!);
            Assert.Equal(1, group.DefaultIndex);
            Assert.Equal(["components/0/HD2IndependentRig", "components/1/本体", "components/1/配套/01"], scope.Components);
            var deploy = ProfileModOptionResolver.ResolveDeployPlan(productManifest,
                Hd2ClConversionService.ProductOptionState(scope.OptionGroups!));
            Assert.Equal([".", "Material/1/2"], deploy.Directories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_IndependentSwitchesWithDifferentModels_FallsBackInsteadOfMergingVariants()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "模型/A/1111111111111111.patch_0");
            Write(root, "模型/B/2222222222222222.patch_0");
            Write(root, "配色/A/9ba626afa44a3aa3.patch_40");
            Write(root, "配色/B/9ba626afa44a3aa3.patch_41");
            var mod = Mod(root, new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Name = "不同模型",
                Options =
                [
                    new ModOption { Name = "A", Include = ["模型/A", "配色/A"] },
                    new ModOption { Name = "B", Include = ["模型/B", "配色/B"] }
                ]
            });

            var scope = Hd2ClConversionService.ResolveConversionScope(mod, null);

            Assert.Null(scope.OptionGroups);
            Assert.Equal(["模型/A", "配色/A", "模型/B", "配色/B"], scope.Components);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_IndependentSwitchesWithMultipleMaterials_FallsBackInsteadOfMakingPartsExclusive()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "模型/1111111111111111.patch_0");
            Write(root, "头发/A/9ba626afa44a3aa3.patch_40");
            Write(root, "头发/B/9ba626afa44a3aa3.patch_41");
            Write(root, "丝袜/A/9ba626afa44a3aa3.patch_42");
            Write(root, "丝袜/B/9ba626afa44a3aa3.patch_43");
            var mod = Mod(root, new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Name = "独立部件开关",
                Options =
                [
                    new ModOption { Name = "头发", Include = ["模型", "头发/A", "头发/B"] },
                    new ModOption { Name = "丝袜", Include = ["模型", "丝袜/A", "丝袜/B"] }
                ]
            });

            var scope = Hd2ClConversionService.ResolveConversionScope(mod, null);

            Assert.Null(scope.OptionGroups);
            Assert.Equal(["模型", "头发/A", "头发/B", "模型", "丝袜/A", "丝袜/B"], scope.Components);
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
