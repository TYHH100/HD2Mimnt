using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class Hd2ClVariantPlannerTests
{
    [Fact]
    public void Plan_GroupsCompanionsAndSharedMaterials()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/5c7587f1808f45b595b7670a35f5cc6d.7805dbf6f83b09ba.rigbin");
            Write(root, "components/1/5c7587f1808f45b595b7670a35f5cc6d.5828025300024f4f.rigbin");
            Write(root, "components/2/7805dbf6f83b09ba.patch_0");
            Write(root, "components/3/5828025300024f4f.patch_0");
            Write(root, "components/4/9ba626afa44a3aa3.patch_32");

            var plans = Hd2ClVariantPlanner.Plan(root);

            Assert.Equal(2, plans.Count);
            Assert.Equal(["components/0", "components/4", "components/2"], plans[0].Folders);
            Assert.Equal(["components/1", "components/4", "components/3"], plans[1].Folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_SingleComponent_ReturnsAllFolders()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/7805dbf6f83b09ba.patch_0");
            Write(root, "components/0/5c7587f1808f45b595b7670a35f5cc6d.7805dbf6f83b09ba.rigbin");

            var plans = Hd2ClVariantPlanner.Plan(root);

            var plan = Assert.Single(plans);
            Assert.Equal("all", plan.Id);
            Assert.Equal(["components/0"], plan.Folders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_UsesComponentManifestName()
    {
        var root = CreateRoot();
        try
        {
            Write(root, "components/0/5c7587f1808f45b595b7670a35f5cc6d.7805dbf6f83b09ba.rigbin");
            Write(root, "components/1/5828025300024f4f.patch_0");
            Write(root, "components/2/7805dbf6f83b09ba.patch_0");
            Write(root, "components/2/manifest.json", """{"Name":"铠甲 B-08"}""");

            var plans = Hd2ClVariantPlanner.Plan(root);

            Assert.Equal(2, plans.Count);
            Assert.Equal("铠甲 B-08", plans[1].Name);
            Assert.Equal("components/2", plans[1].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2cl-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string content = "")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
