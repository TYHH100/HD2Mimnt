using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class HellForgeModSyncServiceTests : IDisposable
{
    private const string BundleX = "9ba626afa44a3aa3";
    private const string BundleY = "047c26f2f11d253e";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-HfSync-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _library;
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly HellForgeCliFixture _fixture = HellForgeCliFixture.Create();
    private readonly SettingsService _settings;
    private readonly HellForgeService _hellForge;
    private readonly HellForgeModSyncService _sync;

    public HellForgeModSyncServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "test.db");
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);
        LoaderRegistry.Initialize(Path.Combine(_root, "appdata"));

        _db = new HD2MimntDbContext(_dbPath);
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new ModRepository(_db);
        _profiles = new ProfileRepository(_db);

        _settings = new SettingsService(Path.Combine(_root, "settings"));
        _fixture.ConfigureAsync(_settings).GetAwaiter().GetResult();
        _hellForge = new HellForgeService(_settings);
        _sync = new HellForgeModSyncService(_hellForge, _profiles, _mods, _settings,
            Path.Combine(_root, "export"), Path.Combine(_root, "appdata", "hellforge-sync.json"));
    }

    [Fact]
    public async Task SyncProfile_ImportsEnabledFlatMods_AndAppliesReversedOrder()
    {
        var a = await CreateFlatMod("A");
        var b = await CreateFlatMod("B");
        var profile = await CreateProfile((a, true, null), (b, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(2, result.SyncedCount);
        Assert.Equal(2, result.UpdatedCount);
        var mods = (await _hellForge.GetModsAsync()).Mods;
        Assert.Equal(2, mods.Count);
        Assert.All(mods, m => Assert.StartsWith("hd2mimnt.", m.Id));
        Assert.All(mods, m => Assert.True(m.Enabled));
        // 加载顺序 A→B 取反置顶：mod-order --ids B,A。
        var orderLine = ArgsLines().Single(l => l.StartsWith("mod-order "));
        Assert.Contains($"hd2mimnt.{b.Id:N},hd2mimnt.{a.Id:N}", orderLine);
        // 扁平化内容确实落位 mods\<id>\data。
        Assert.True(File.Exists(Path.Combine(_fixture.Root, "mods", a.Id.ToString("N"), "data", $"{BundleX}.patch_0")));
        // 同步绝不写 game\data。
        Assert.Empty(Directory.GetFiles(Path.Combine(_fixture.Root, "game", "data")));
    }

    [Fact]
    public async Task SyncProfile_SkipsDisabledMods()
    {
        var a = await CreateFlatMod("A");
        var b = await CreateFlatMod("B");
        var profile = await CreateProfile((a, true, null), (b, false, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, result.SyncedCount);
        var mods = (await _hellForge.GetModsAsync()).Mods;
        Assert.Single(mods);
        Assert.Equal($"hd2mimnt.{a.Id:N}", mods[0].Id);
    }

    [Fact]
    public async Task SyncProfile_SkipsLoadersAndIas_WithReasons()
    {
        var normal = await CreateFlatMod("Normal");
        var loader = await CreateFlatMod("Loader", mod => { mod.BslRole = BslModRole.Loader; mod.BslDetectionVersion = int.MaxValue; });
        var ias = await CreateFlatMod("Ias", mod => mod.WorkflowType = "HD2IAS");
        var profile = await CreateProfile((normal, true, null), (loader, true, null), (ias, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.PartiallyFailed, result.Status);
        Assert.Equal(1, result.SyncedCount);
        Assert.Equal(2, result.SkippedMods.Count);
        Assert.Contains(result.SkippedMods, s => s.Name == "Loader" && s.Reason.Contains("加载器"));
        Assert.Contains(result.SkippedMods, s => s.Name == "Ias" && s.Reason.Contains("HD2IAS"));
    }

    [Fact]
    public async Task SyncProfile_SessionRunning_Refuses()
    {
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));
        _fixture.OverwriteResponse("status", """
            { "hellforge": "0.4.0-beta", "ready": { "gameCopy": true, "sessionRunner": true },
              "session": { "running": true, "lastStatus": "observing" } }
            """);

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("会话正在运行", result.ErrorMessage);
        Assert.DoesNotContain(ArgsLines(), l => l.StartsWith("mod-add "));
    }

    [Fact]
    public async Task SyncProfile_UnchangedMod_SkipsReexport()
    {
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));

        var first = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);
        var second = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, first.UpdatedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.UnchangedCount);
        Assert.Single(ArgsLines().Where(l => l.StartsWith("mod-add ")));
    }

    [Fact]
    public async Task SyncProfile_RemovesStaleOwnedMods()
    {
        var a = await CreateFlatMod("A");
        var b = await CreateFlatMod("B");
        var profile = await CreateProfile((a, true, null), (b, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 把 B 移出配置（禁用），再同步 → B 应被 mod-remove。
        profile.Mods.Single(m => m.ModId == b.Id).Enabled = false;
        await _profiles.UpdateAsync(profile);
        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, result.RemovedCount);
        Assert.Contains(ArgsLines(), l => l.StartsWith($"mod-remove --id hd2mimnt.{b.Id:N}"));
        var mods = (await _hellForge.GetModsAsync()).Mods;
        Assert.Single(mods);
        Assert.Equal($"hd2mimnt.{a.Id:N}", mods[0].Id);
    }

    [Fact]
    public async Task SyncProfile_PackPassthrough_KeepsOptionsAndAppliesChoice()
    {
        var pack = await CreatePackMod("Pack",
            new ModOption { Name = "A", Include = new List<string> { "A" } },
            new ModOption { Name = "B", Include = new List<string> { "B" } });
        var optionsJson = new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } }.ToJson();
        var profile = await CreateProfile((pack, true, optionsJson));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var mods = (await _hellForge.GetModsAsync()).Mods;
        var info = Assert.Single(mods);
        Assert.StartsWith("pack.", info.Id);
        Assert.NotNull(info.Options);
        Assert.True(info.Options!.Single(o => o.Name == "A").On);
        Assert.False(info.Options.Single(o => o.Name == "B").On);
        // pack 透传：mods\ 里保留作者 manifest 与 Include 目录。
        Assert.True(File.Exists(Path.Combine(info.Folder!, "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(info.Folder!, "B")));
    }

    [Fact]
    public async Task SyncProfile_PackVariant_AppliesSubOption()
    {
        var pack = await CreatePackMod("Pack", new ModOption
        {
            Name = "A", Include = new List<string> { "A" },
            SubOptions = new List<ModSubOption>
            {
                new() { Name = "V1", Include = new List<string> { "A" } },
                new() { Name = "V2", Include = new List<string> { "A" } }
            }
        });
        var optionsJson = new ProfileModOptionsState
        {
            OptionToggles = new List<bool> { true },
            OptionSelections = new List<int> { 1 }
        }.ToJson();
        var profile = await CreateProfile((pack, true, optionsJson));

        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        var info = Assert.Single((await _hellForge.GetModsAsync()).Mods);
        Assert.Equal("V2", info.Options!.Single(o => o.Name == "A").Picked);
        Assert.Contains(ArgsLines(), l => l.StartsWith("mod-pack ") && l.Contains("--variant V2"));
    }

    [Fact]
    public async Task RemoveSynced_RemovesAllOwnedMods()
    {
        var a = await CreateFlatMod("A");
        var b = await CreateFlatMod("B");
        var profile = await CreateProfile((a, true, null), (b, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        var result = await _sync.RemoveSyncedAsync();

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(2, result.RemovedCount);
        Assert.Empty((await _hellForge.GetModsAsync()).Mods);
    }

    [Fact]
    public async Task GetStatusAsync_ReflectsSync()
    {
        var before = await _sync.GetStatusAsync();
        Assert.False(before.HasSync);

        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        var after = await _sync.GetStatusAsync();
        Assert.True(after.HasSync);
        Assert.Equal(1, after.ModCount);
        Assert.Equal(profile.Id.ToString("N"), after.ProfileId);
    }

    private IEnumerable<string> ArgsLines() =>
        _fixture.ReadArgs().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());

    private async Task<Mod> CreateFlatMod(string name, Action<Mod>? configure = null)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_library, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = new ModManifest { Guid = id, Name = name };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(Path.Combine(directory, $"{BundleX}.patch_0"), name + "-content");
        var mod = new Mod
        {
            Id = id, Name = name, Version = "1.0.0", Path = directory, Manifest = manifest,
            Category = ModCategory.Gameplay, ImportedAt = DateTime.UtcNow, ThumbnailPath = string.Empty
        };
        configure?.Invoke(mod);
        await _mods.AddAsync(mod);
        return mod;
    }

    private async Task<Mod> CreatePackMod(string name, params ModOption[] options)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_library, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = new ModManifest
        {
            Guid = id, Name = name, ManifestVersion = ManifestVersion.V1, Options = options.ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
        foreach (var relative in options.SelectMany(o => o.Include ?? new List<string>())
                     .Concat(options.SelectMany(o => o.SubOptions?.SelectMany(s => s.Include ?? new List<string>()) ?? new List<string>()))
                     .Distinct())
        {
            var optionDir = Path.Combine(directory, relative);
            Directory.CreateDirectory(optionDir);
            await File.WriteAllTextAsync(Path.Combine(optionDir, $"{BundleY}.patch_0"), name + "-" + relative);
        }
        var mod = new Mod
        {
            Id = id, Name = name, Version = "1.0.0", Path = directory, Manifest = manifest,
            Category = ModCategory.Gameplay, ImportedAt = DateTime.UtcNow, ThumbnailPath = string.Empty
        };
        await _mods.AddAsync(mod);
        return mod;
    }

    private async Task<Profile> CreateProfile(params (Mod Mod, bool Enabled, string? OptionsJson)[] members)
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(), Name = "Test", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            Mods = members.Select((m, i) => new ProfileMod
            {
                ModId = m.Mod.Id, Enabled = m.Enabled, Order = i, OptionsJson = m.OptionsJson ?? string.Empty
            }).ToList()
        };
        await _profiles.AddAsync(profile);
        return profile;
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}