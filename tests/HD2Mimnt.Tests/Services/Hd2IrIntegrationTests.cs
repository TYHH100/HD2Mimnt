using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class Hd2IrIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-hd2ir-test-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly DeploymentRepository _deployments;
    private readonly ModImportService _import;
    private readonly DeploymentService _deployment;

    public Hd2IrIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new(_db); _profiles = new(_db); _deployments = new(_db);
        _import = new(_mods, Path.Combine(_root, "library"), tempDirectory: Path.Combine(_root, "temp"));
        _deployment = new(_profiles, _mods, _deployments, [new CopyDeploymentMethod()]);
    }

    [Fact]
    public async Task NestedImport_SourceRemoved_ProfileSelectionDeploysAndCleansBothTargets()
    {
        var source = CreateNestedPackage();
        Hd2IrImportPreview? preview = null;
        var id = await _import.ImportModAsync(source, confirmHd2Ir: (value, _) => { preview = value; return Task.CompletedTask; });
        Assert.NotNull(preview);
        Assert.Equal(2, preview.Components.Count);
        File.Delete(source);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")));
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal("HD2IR", mod.WorkflowType);
        Assert.Equal(2, mod.Manifest!.Options!.Count);
        var game = Path.Combine(_root, "game"); Directory.CreateDirectory(game);
        var full = await CreateProfile(id, [true, true]);
        var rigOnly = await CreateProfile(id, [true, false]);
        var patchOnly = await CreateProfile(id, [false, true]);
        var native = Path.Combine(game, "bin", "HD2IndependentRig", "hero.rigbin");
        var patch = Path.Combine(game, "data", "0123456789abcdef.patch_0");

        Assert.Equal(DeploymentStatus.Completed, (await Deploy(full, game)).Status);
        Assert.Equal("rig", await File.ReadAllTextAsync(native));
        Assert.Equal("patch", await File.ReadAllTextAsync(patch));
        var records = (await _deployment.GetLatestDeploymentsAsync()).ToList();
        var files = (await _deployment.GetDeploymentFilesAsync(Assert.Single(records).Id)).ToList();
        Assert.Contains(files, file => file.TargetPath == native);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(rigOnly, game)).Status);
        Assert.True(File.Exists(native)); Assert.False(File.Exists(patch));
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(patchOnly, game)).Status);
        Assert.False(File.Exists(native)); Assert.True(File.Exists(patch));
        await _deployment.PreDeploymentCleanupAsync();
        Assert.False(File.Exists(patch));
        Assert.Empty(await _deployment.GetLatestDeploymentsAsync());
    }

    [Fact]
    public async Task ImportSameNestedPackageTwice_RejectsDuplicateDespiteGeneratedMetadataIds()
    {
        var source = CreateNestedPackage();
        await _import.ImportModAsync(source, confirmHd2Ir: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _import.ImportModAsync(source, confirmHd2Ir: (_, _) => Task.CompletedTask));
        Assert.Single(await _mods.GetAllAsync());
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "library")));
    }

    [Fact]
    public async Task CancelDuringConfirmation_LeavesNoModOrTemporaryFiles()
    {
        var source = CreateNestedPackage();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _import.ImportModAsync(source, cancellation.Token,
            confirmHd2Ir: (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.Empty(await _mods.GetAllAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "library")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")));
    }

    [Fact]
    public async Task NativeTargetAlreadyExists_PreflightPreservesOriginalFile()
    {
        var source = CreateNestedPackage();
        var id = await _import.ImportModAsync(source, confirmHd2Ir: (_, _) => Task.CompletedTask);
        var profile = await CreateProfile(id, [true, true]);
        var game = Path.Combine(_root, "game");
        var native = Path.Combine(game, "bin", "HD2IndependentRig", "hero.rigbin");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        await File.WriteAllTextAsync(native, "external");
        var result = await Deploy(profile, game);
        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("不属于当前部署", result.ErrorMessage);
        Assert.Equal("external", await File.ReadAllTextAsync(native));
        Assert.Empty(await _deployment.GetLatestDeploymentsAsync());
    }

    [Fact]
    public async Task StandardHd2IrPack_ImportsWithoutConfirmation()
    {
        var directory = Path.Combine(_root, "rig");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "hero.rigbin"), "rig");
        var archive = Path.Combine(_root, "Hero.hd2irpack.zip");
        ZipFile.CreateFromDirectory(directory, archive);
        var id = await _import.ImportModAsync(archive);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal("Hero", mod.Name);
        Assert.Equal("HD2IR", mod.WorkflowType);
        Assert.Single(mod.Manifest!.Options!);
    }

    [Fact]
    public async Task ExistingManifest_PreservesOriginalJsonAndOnlyDeploysSelectedVariant()
    {
        var source = Path.Combine(_root, "with-options");
        Directory.CreateDirectory(Path.Combine(source, "bin", "HD2IndependentRig"));
        Directory.CreateDirectory(Path.Combine(source, "A")); Directory.CreateDirectory(Path.Combine(source, "B"));
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "HD2IndependentRig", "hero.rigbin"), "rig");
        await File.WriteAllTextAsync(Path.Combine(source, "A", "0123456789abcdef.patch_0"), "A");
        await File.WriteAllTextAsync(Path.Combine(source, "B", "0123456789abcdef.patch_0"), "B");
        const string json = """{"Version":1,"Name":"Original","IconPath":"cover.png","Options":[{"Name":"Variant","SubOptions":[{"Name":"A","Include":["A"],"Image":"cover.png"},{"Name":"B","Include":["B"]}]}]}""";
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), json);
        await File.WriteAllBytesAsync(Path.Combine(source, "cover.png"), [1, 2, 3]);
        var id = await _import.ImportModAsync(source, confirmHd2Ir: (_, _) => Task.CompletedTask);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal("Original", mod.Name);
        Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(mod.Path, "components", "1", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(mod.Path, mod.Manifest!.IconPath!)));
        Assert.True(File.Exists(Path.Combine(mod.Path, mod.Manifest.Options![1].SubOptions![0].Image!)));
        var profile = await CreateProfile(id, [true, true]);
        var stored = (await _profiles.GetByIdAsync(profile))!;
        stored.Mods[0].OptionsJson = new ProfileModOptionsState { OptionToggles = [true, true], OptionSelections = [0, 1] }.ToJson();
        await _profiles.UpdateAsync(stored);
        var game = Path.Combine(_root, "game"); Directory.CreateDirectory(game);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(profile, game)).Status);
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(game, "data", "0123456789abcdef.patch_0")));
        Assert.False(File.Exists(Path.Combine(game, "data", "0123456789abcdef.patch_1")));
    }

    [Fact]
    public async Task UpdatePackage_IsNotImportedAsCharacterMod()
    {
        var source = Path.Combine(_root, "update");
        Directory.CreateDirectory(Path.Combine(source, "bin", "HD2IndependentRig"));
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "HD2IndependentRig", "runtime.lua"), "runtime");
        await File.WriteAllTextAsync(Path.Combine(source, "update_notes.json"), "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => _import.ImportModAsync(source));
        Assert.Empty(await _mods.GetAllAsync());
    }

    private string CreateNestedPackage()
    {
        var rig = Path.Combine(_root, "rig-source", "bin", "HD2IndependentRig");
        Directory.CreateDirectory(rig);
        File.WriteAllText(Path.Combine(rig, "hero.rigbin"), "rig");
        var resources = Path.Combine(_root, "resources-source"); Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "0123456789abcdef.patch_9"), "patch");
        var outer = Path.Combine(_root, "outer"); Directory.CreateDirectory(outer);
        ZipFile.CreateFromDirectory(Path.Combine(_root, "rig-source"), Path.Combine(outer, "Hero.hd2irpack.zip"));
        var inner = Path.Combine(_root, "inner"); Directory.CreateDirectory(inner);
        ZipFile.CreateFromDirectory(resources, Path.Combine(inner, "Hero-model.zip"));
        ZipFile.CreateFromDirectory(inner, Path.Combine(outer, "Resources.zip"));
        var source = Path.Combine(_root, "Hero.zip"); ZipFile.CreateFromDirectory(outer, source);
        return source;
    }

    private async Task<Guid> CreateProfile(Guid modId, List<bool> toggles)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = $"test-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
        profile.Mods.Add(new ProfileMod { ProfileId = profile.Id, ModId = modId, Enabled = true,
            OptionsJson = new ProfileModOptionsState { OptionToggles = toggles }.ToJson() });
        await _profiles.AddAsync(profile);
        return profile.Id;
    }

    private Task<DeploymentResult> Deploy(Guid profile, string game)
        => _deployment.DeployProfileAsync(profile, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
