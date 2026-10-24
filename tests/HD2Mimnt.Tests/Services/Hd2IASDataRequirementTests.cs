using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Tests.Services;

public sealed partial class Hd2IASIntegrationTests
{
    [Fact]
    public async Task DataRequirement_NoRuntimeOrData_DoesNotPrompt()
    {
        var game = CreateGame("native"); var (service, _, _) = await InstallerAsync();
        var state = await service.GetManagementStateAsync(game);
        Assert.False(state.HasDataMod); Assert.False(state.HasDataFiles);
        Assert.False(state.NeedsDataGeneration); Assert.False(state.NeedsInstallation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataRequirement_RuntimeWithoutLibraryEntry_PromptsForData(bool managed)
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync();
        if (managed)
        {
            var installed = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
            await _iasModRepository.DeleteAsync(installed.ModId!.Value);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json"), BuildContents("1.4.4", "native"));
            await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.addon64"), "native-addon");
        }
        var state = await service.GetManagementStateAsync(game);
        Assert.False(state.HasDataMod); Assert.False(state.HasDataFiles);
        Assert.True(state.NeedsDataGeneration); Assert.False(state.NeedsInstallation);
        Assert.Equal(managed, state.HasManagedInstallation);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("patch")]
    [InlineData("empty")]
    [InlineData("invalid")]
    public async Task DataRequirement_MissingOrInvalidPatch_RepairsSameVersionAndKeepsIdentityAndProfile(string damage)
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync();
        var installed = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        var id = installed.ModId!.Value;
        var profile = new Profile { Id = Guid.NewGuid(), Name = "data repair" };
        profile.Mods.Add(new() { ModId = id, ProfileId = profile.Id, Enabled = false, Order = 7, OptionsJson = "{\"custom\":true}" });
        await _iasProfiles.AddAsync(profile);
        var patch = Path.Combine(installed.Path, "data", StingrayArchiveWriter.PatchName);
        if (damage == "directory") Directory.Delete(installed.Path, true);
        else if (damage == "patch") File.Delete(patch);
        else await File.WriteAllTextAsync(patch, damage == "empty" ? "" : "invalid patch");

        var missing = await service.GetManagementStateAsync(game);
        Assert.True(missing.HasDataMod); Assert.False(missing.HasDataFiles);
        Assert.True(missing.NeedsDataGeneration); Assert.False(missing.NeedsInstallation);

        var repaired = await service.InstallAsync(zip, game, Hd2IASRoute.Native);
        Assert.Equal(id, repaired.ModId); Assert.Equal(id, Assert.Single(await _iasMods.GetAllModsAsync()).Id);
        var state = await service.GetManagementStateAsync(game);
        Assert.True(state.HasDataFiles); Assert.False(state.NeedsDataGeneration);
        var member = Assert.Single((await _iasProfiles.GetByIdAsync(profile.Id))!.Mods);
        Assert.False(member.Enabled); Assert.Equal(7, member.Order); Assert.Equal("{\"custom\":true}", member.OptionsJson);
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
    }

    [Fact]
    public async Task DataRequirement_LegacyRootPatchAndEmptyCompanionsWithoutGameDeployment_AreAvailable()
    {
        var zip = CreatePackage(includeBuild: true); var game = CreateGame("native");
        var (service, _, _) = await InstallerAsync();
        var exported = await service.GenerateAsync(zip, game, Hd2IASRoute.Native, Path.Combine(_root, "legacy-data.zip"));
        var legacy = Path.Combine(_root, "legacy-root"); Directory.CreateDirectory(legacy);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(exported.Path))
        {
            using var source = archive.GetEntry("data/" + StingrayArchiveWriter.PatchName)!.Open();
            using var target = File.Create(Path.Combine(legacy, StingrayArchiveWriter.PatchName));
            await source.CopyToAsync(target);
        }
        await File.WriteAllBytesAsync(Path.Combine(legacy, StingrayArchiveWriter.PatchName + ".stream"), []);
        await File.WriteAllBytesAsync(Path.Combine(legacy, StingrayArchiveWriter.PatchName + ".gpu_resources"), []);
        await _iasMods.ImportModAsync(legacy);
        var state = await service.GetManagementStateAsync(game);
        Assert.True(state.HasDataFiles); Assert.False(state.NeedsDataGeneration);
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
    }
}
