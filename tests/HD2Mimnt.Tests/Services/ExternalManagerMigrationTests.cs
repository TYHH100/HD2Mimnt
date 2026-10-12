using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Migration;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Tests.Services;

public sealed class ExternalManagerMigrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-migration-test-{Guid.NewGuid():N}");
    private HD2MimntDbContext _db = null!;
    private ModRepository _modRepository = null!;
    private ProfileRepository _profileRepository = null!;
    private ModService _mods = null!;
    private ProfileService _profiles = null!;
    private SettingsService _settings = null!;
    private ExternalManagerMigrationService _migration = null!;
    private string Library => Path.Combine(_root, "target-library");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _db = new HD2MimntDbContext(Path.Combine(_root, "target.db"));
        await _db.InitializeDatabaseAsync();
        _modRepository = new ModRepository(_db);
        _profileRepository = new ProfileRepository(_db);
        _mods = new ModService(_modRepository, Library, tempDirectory: Path.Combine(_root, "temp"));
        _profiles = new ProfileService(_profileRepository, _modRepository);
        _settings = new SettingsService(new SettingsRepository(_db.DatabasePath), _root);
        _migration = new ExternalManagerMigrationService([new Helldivers2ModManagerReader(), new EchelonReader(), new Hd2ArsenalReader()],
            _mods, _profiles, _settings);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Arsenal_MultipleProfiles_PreservesIndependentChoicesAndSeparators()
    {
        var a = SourceMod("a");
        var b = SourceMod("b");
        var source = Path.Combine(_root, "arsenal");
        WriteJson(Path.Combine(source, "hd2a_data.json"), new
        {
            modsLibrary = new object[]
            {
                new { uuid = "a", path = a.SourcePath, label = "A", options = new[] { new { name = "配件", include = new[] { "var" }, enabled = true,
                    suboptions = new[] { new { name = "红", include = new[] { "red" } }, new { name = "蓝", include = new[] { "blue" } } } } } },
                new { uuid = "b", path = b.SourcePath, label = "B", options = Array.Empty<object>() }
            },
            modsList = new Dictionary<string, object>
            {
                ["one"] = new { label = "战斗", mods = new object[] {
                    new { type = "separator", title = "武器", backgroundColor = "#123456", collapsed = true },
                    new { uuid = "a", enabled = true, optionsConfig = new[] { new { name = "配件", enabled = false, selected = 1 } } },
                    new { uuid = "b", enabled = false } } },
                ["two"] = new { label = "备用", mods = new object[] { new { uuid = "b", enabled = true },
                    new { uuid = "a", enabled = false, options = new[] { new { name = "配件", enabled = true, selected = 0 } } } } }
            },
            profileOrder = new[] { "two", "one" }, selectedProfile = "one", setTopPriority = false
        });
        var originalId = await _profiles.CreateProfileAsync("战斗");
        await _profiles.InitializeAsync();
        var notifications = 0;
        _profiles.ProfilesChanged += (_, _) => notifications++;
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.HD2Arsenal, source));
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        Assert.Equal(2, result.ImportedMods);
        Assert.Equal(2, result.ImportedProfiles);
        Assert.Equal(2, notifications);
        Assert.Equal("two", plan.Profiles[0].SourceId);
        var one = (await _profileRepository.GetByIdAsync(result.ProfileIds["one"]))!;
        Assert.Equal("战斗 (2)", one.Name);
        Assert.Equal(result.ProfileIds["one"], result.SourceActiveProfileId);
        Assert.Equal(originalId, (await _profiles.GetActiveProfileAsync())!.Id);
        var aMember = one.Mods.Single(item => item.ModId == result.ModIds["a"]);
        Assert.True(aMember.Enabled);
        Assert.Equal(one.Id, aMember.ProfileId);
        Assert.False(ProfileModOptionsState.FromJson(aMember.OptionsJson).OptionToggles![0]);
        Assert.Equal(1, ProfileModOptionsState.FromJson(aMember.OptionsJson).OptionSelections![0]);
        var separator = Assert.Single(one.Separators);
        Assert.Equal(("武器", 0, 2, true, "#123456"), (separator.Name, separator.Order, separator.MemberCount, separator.IsCollapsed, separator.Color));
        var two = (await _profileRepository.GetByIdAsync(result.ProfileIds["two"]))!;
        Assert.Equal(result.ModIds["b"], two.Mods[0].ModId);
        Assert.True(ProfileModOptionsState.FromJson(two.Mods[1].OptionsJson).OptionToggles![0]);
    }

    [Fact]
    public async Task Arsenal_LibrarySeparator_PreservesOrganizationAcrossFixedLuaBoundary()
    {
        var loader = SourceMod("loader", Guid.Parse("612eaf70-d682-43c7-9efd-16dcc695f977"));
        var a = SourceMod("a");
        var b = SourceMod("b");
        new ManifestParser().SaveToFile(loader.Manifest, Path.Combine(loader.SourcePath, "manifest.json"));
        var source = Path.Combine(_root, "arsenal");
        WriteJson(Path.Combine(source, "hd2a_data.json"), new
        {
            modsLibrary = new object[] {
                new { uuid = "loader", path = loader.SourcePath, label = "加载器" },
                new { type = "separator", label = "黄色分组", backgroundColor = "#fee800", collapsed = true },
                new { uuid = "a", path = a.SourcePath, label = "A" },
                new { uuid = "b", path = b.SourcePath, label = "B" } },
            modsList = new Dictionary<string, object>()
        });
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.HD2Arsenal, source));
        Assert.Equal(1, plan.Profiles.Single().Separators.Single().Order);
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        var layout = (await _profileRepository.GetByIdAsync(result.ProfileIds["library-layout"]))!;
        Assert.Equal(new[] { result.ModIds["a"], result.ModIds["b"], result.ModIds["loader"] }, layout.Mods.Select(mod => mod.ModId));
        Assert.All(layout.Mods, mod => Assert.False(mod.Enabled));
        var separator = Assert.Single(layout.Separators);
        Assert.Equal(("黄色分组", 0, (int?)2, "#FEE800", true),
            (separator.Name, separator.Order, separator.MemberCount, separator.Color, separator.IsCollapsed));
    }

    [Fact]
    public async Task HD2MM_LegacyAndV1_PreservesPerProfileStateTagsAndFixedBoundary()
    {
        var legacy = SourceMod("legacy", legacy: true);
        var v1 = SourceMod("v1");
        v1.Manifest.Options = [new() { Name = "外观", Include = [], SubOptions = [new() { Name = "一", Include = [] }, new() { Name = "二", Include = [] }] },
            new() { Name = "配件", Include = [], DefaultEnabled = false }];
        var loader = SourceMod("loader", Guid.Parse("612eaf70-d682-43c7-9efd-16dcc695f977"));
        var source = Path.Combine(_root, "hd2mm");
        foreach (var mod in new[] { legacy, v1, loader })
        {
            var target = Path.Combine(source, "Mods", mod.Manifest.Guid.ToString());
            Directory.CreateDirectory(target);
            File.Copy(Path.Combine(mod.SourcePath, "1234567890abcdef.patch_0"), Path.Combine(target, "1234567890abcdef.patch_0"));
            new ManifestParser().SaveToFile(mod.Manifest, Path.Combine(target, "manifest.json"));
        }
        WriteJson(Path.Combine(source, "settings.json"), new
        {
            StorageDirectory = source, DeployBottomToTop = false, Tags = new[] { new { id = "tag", name = "盔甲" } },
            OptionOrders = new Dictionary<string, int[]> { [legacy.Manifest.Guid.ToString()] = [1, 0], [v1.Manifest.Guid.ToString()] = [1, 0] },
            SubOptionOrders = new Dictionary<string, object> { [v1.Manifest.Guid.ToString()] = new Dictionary<string, int[]> { ["0"] = [1, 0] } },
            Separators = new[] { new { name = "装备", displayIndex = 0, isExpanded = false, color = "#FF123456", modGuids = Array.Empty<string>() } }
        });
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(source, "mod_manager.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE mod_groups(Id TEXT,Name TEXT,DisplayIndex INT,ModGuids TEXT);
                CREATE TABLE enabled_mods(Guid TEXT,Enabled INT,Toggled TEXT,Selected TEXT,SortOrder INT,TagIds TEXT);
                CREATE TABLE group_enabled_mods(GroupId TEXT,Guid TEXT,Enabled INT,Toggled TEXT,Selected TEXT,SortOrder INT);
                """;
            await command.ExecuteNonQueryAsync();
            foreach (var group in new[] { "one", "two" })
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO mod_groups VALUES(@group,@group,0,@members)";
                command.Parameters.AddWithValue("@group", group);
                command.Parameters.AddWithValue("@members", JsonSerializer.Serialize(new[] { loader.Manifest.Guid, legacy.Manifest.Guid, v1.Manifest.Guid }));
                await command.ExecuteNonQueryAsync();
                foreach (var (mod, order) in new[] { (loader, 0), (legacy, 1), (v1, 2) })
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO group_enabled_mods VALUES(@group,@id,@on,@toggles,@selections,@order)";
                    command.Parameters.AddWithValue("@group", group);
                    command.Parameters.AddWithValue("@id", mod.Manifest.Guid.ToString());
                    command.Parameters.AddWithValue("@on", group == "one" ? 1 : 0);
                    command.Parameters.AddWithValue("@toggles", group == "one" ? "[false,true]" : "[true,false]");
                    command.Parameters.AddWithValue("@selections", group == "one" ? "[1,0]" : "[0,0]");
                    command.Parameters.AddWithValue("@order", order);
                    await command.ExecuteNonQueryAsync();
                }
            }
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO enabled_mods VALUES(@id,1,'[]','[]',0,'[\"tag\"]')";
            command.Parameters.AddWithValue("@id", legacy.Manifest.Guid.ToString());
            await command.ExecuteNonQueryAsync();
        }
        var result = await _migration.MigrateAsync(await _migration.PreviewAsync(new(ExternalManagerKind.Helldivers2ModManager, source, source)));
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        var one = (await _profileRepository.GetByIdAsync(result.ProfileIds["one"]))!;
        Assert.Equal(result.ModIds[loader.Manifest.Guid.ToString()], one.Mods.Last().ModId);
        Assert.Equal(2, Assert.Single(one.Separators).MemberCount);
        Assert.True(one.Separators[0].IsCollapsed);
        var importedLegacy = (await _modRepository.GetByIdAsync(result.ModIds[legacy.Manifest.Guid.ToString()]))!;
        Assert.Contains("盔甲", importedLegacy.Tags);
        Assert.Equal(ManifestVersion.Legacy, new ManifestParser().ParseFromFile(Path.Combine(importedLegacy.Path, "manifest.json"))!.ManifestVersion);
        Assert.Equal("High", importedLegacy.Manifest!.LegacyOptions![0]);
        Assert.Equal(0, ProfileModOptionsState.FromJson(one.Mods[0].OptionsJson).LegacySelectedIndex);
        var importedV1 = (await _modRepository.GetByIdAsync(result.ModIds[v1.Manifest.Guid.ToString()]))!;
        var importedOptions = importedV1.Manifest!.Options!;
        Assert.Equal(new[] { "配件", "外观" }, importedOptions.Select(option => option.Name));
        Assert.Equal(new[] { "二", "一" }, importedOptions[1].SubOptions!.Select(option => option.Name));
        Assert.Equal(new[] { 0, 0 }, ProfileModOptionsState.FromJson(one.Mods[1].OptionsJson).OptionSelections);
        Assert.Equal(new[] { true, false }, ProfileModOptionsState.FromJson(one.Mods[1].OptionsJson).OptionToggles);
        var two = (await _profileRepository.GetByIdAsync(result.ProfileIds["two"]))!;
        Assert.All(two.Mods, item => Assert.False(item.Enabled));
        Assert.Equal(1, ProfileModOptionsState.FromJson(two.Mods[0].OptionsJson).LegacySelectedIndex);
        Assert.Equal(new[] { 0, 1 }, ProfileModOptionsState.FromJson(two.Mods[1].OptionsJson).OptionSelections);
        Assert.Equal(new[] { false, true }, ProfileModOptionsState.FromJson(two.Mods[1].OptionsJson).OptionToggles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HD2MM_CustomDeploymentOrder_PreservesGroupsOrReportsUnrepresentableLayout(bool splitGroup)
    {
        var source = Path.Combine(_root, "hd2mm");
        var definitions = Enumerable.Range(0, 4).Select(index => SourceMod($"mod-{index}")).ToArray();
        foreach (var mod in definitions)
        {
            var path = Path.Combine(source, "Mods", mod.Manifest.Guid.ToString());
            Directory.CreateDirectory(path);
            File.Copy(Path.Combine(mod.SourcePath, "1234567890abcdef.patch_0"), Path.Combine(path, "1234567890abcdef.patch_0"));
            new ManifestParser().SaveToFile(mod.Manifest, Path.Combine(path, "manifest.json"));
        }
        var ids = definitions.Select(mod => mod.Manifest.Guid.ToString()).ToArray();
        var order = splitGroup ? new[] { ids[0], ids[2], ids[1], ids[3] } : new[] { ids[2], ids[3], ids[1], ids[0] };
        WriteJson(Path.Combine(source, "settings.json"), new
        {
            StorageDirectory = source, UseDeploymentOrder = true, DeploymentOrderGuids = order,
            Separators = new[] {
                new { name = "第一组", displayIndex = 0, isExpanded = false, color = "#123456", modGuids = ids.Take(2).ToArray() },
                new { name = "第二组", displayIndex = 3, isExpanded = true, color = "#987654", modGuids = ids.Skip(2).ToArray() } }
        });
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(source, "mod_manager.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE mod_groups(Id TEXT,Name TEXT,DisplayIndex INT,ModGuids TEXT)";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO mod_groups VALUES('p','自定义排序',0,@members)";
            command.Parameters.AddWithValue("@members", JsonSerializer.Serialize(ids));
            await command.ExecuteNonQueryAsync();
        }
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.Helldivers2ModManager, source, source));
        var result = await _migration.MigrateAsync(plan);
        Assert.Equal(4, result.ImportedMods);
        if (splitGroup)
        {
            Assert.Empty(result.ProfileIds);
            Assert.Contains(result.Issues, issue => issue.Message.Contains("分隔符成员不连续"));
        }
        else
        {
            Assert.True(result.IsComplete, string.Join(";", result.Issues));
            var profile = (await _profileRepository.GetByIdAsync(result.ProfileIds["p"]))!;
            Assert.Equal(order.Select(id => result.ModIds[id]), profile.Mods.Select(mod => mod.ModId));
            Assert.Equal(new[] { ("第二组", 0, (int?)2), ("第一组", 2, (int?)2) },
                profile.Separators.Select(separator => (separator.Name, separator.Order, separator.MemberCount)));
            Assert.True(profile.Separators.Single(separator => separator.Name == "第一组").IsCollapsed);
        }
    }

    [Fact]
    public async Task Echelon_SetsInSameDirectory_DeploysOnlyChosenSet()
    {
        var source = Path.Combine(_root, "echelon");
        var store = Path.Combine(_root, "echelon-store");
        foreach (var index in Enumerable.Range(0, 3)) WriteFile(Path.Combine(store, "m", "shared", $"1234567890abcde{index}.patch_0"), $"set {index}");
        WriteJson(Path.Combine(source, "locations.json"), new { mods = Path.Combine(_root, "missing") });
        WriteJson(Path.Combine(source, "state.json"), new
        {
            version = 2, library = new { m = new { id = "m", store = "m", name = "装甲", tags = new[] { "外观" },
                sets = Enumerable.Range(0, 3).Select(index => new { files = new Dictionary<string, string> { [""] = $"shared/1234567890abcde{index}.patch_0" }, folder = "wrong" }).ToArray(),
                options = new[] { new { name = "皮肤", include = Array.Empty<int>(), subs = new[] { new { name = "红", include = new[] { 0 } }, new { name = "蓝", include = new[] { 1 } } } } },
                option_defaults = new { on = new[] { false }, sel = new[] { 0 } } } },
            profiles = new[] { new { id = "p", name = "装甲配置", entries = new object[] {
                new { type = "sep", title = "盔甲", bg = "#987654", collapsed = true },
                new { type = "mod", id = "m", enabled = true, options = new { on = new[] { true }, sel = new[] { 1 } } },
                new { type = "sep", title = "Lua", bg = "#111111", collapsed = true, bingus = true, auto = true } } } },
            active_profile = "p", settings = new { priority = "last" }
        });
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.Echelon, source, ModsPath: store));
        Assert.Equal(Path.Combine(store, "m"), plan.Mods[0].SourcePath);
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        var mod = (await _modRepository.GetByIdAsync(result.ModIds["m"]))!;
        var profile = (await _profileRepository.GetByIdAsync(result.ProfileIds["p"]))!;
        var game = Path.Combine(_root, "test-game");
        Directory.CreateDirectory(game);
        var deployment = new DeploymentService(_profileRepository, _modRepository, new DeploymentRepository(_db), [new CopyDeploymentMethod()]);
        var deployed = await deployment.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);
        Assert.Equal(DeploymentStatus.Completed, deployed.Status);
        var selected = Directory.GetFiles(Path.Combine(game, "data"), "*.patch_0");
        Assert.Equal("1234567890abcde1.patch_0", Path.GetFileName(Assert.Single(selected)));
        Assert.Equal("set 1", File.ReadAllText(selected[0]));
        Assert.True(profile.IsLuaGroupCollapsed);
        Assert.Single(profile.Separators);
        Assert.True(profile.Separators[0].IsCollapsed);
    }

    [Fact]
    public async Task Migrate_RepeatedAndChangedContent_DoesNotOverwriteUserModsOrProfiles()
    {
        var mod = SourceMod("same");
        new ManifestParser().SaveToFile(mod.Manifest, Path.Combine(mod.SourcePath, "manifest.json"));
        var userMod = await _mods.ImportModAsync(mod.SourcePath);
        var plan = Plan(mod);
        var first = await _migration.MigrateAsync(plan);
        Assert.NotEqual(userMod, first.ModIds["same"]);
        var profileId = first.ProfileIds["p"];
        await _profiles.RenameProfileAsync(profileId, "用户改名");
        var again = await _migration.MigrateAsync(plan);
        Assert.Equal(1, again.ReusedMods);
        Assert.Equal(1, again.ReusedProfiles);
        Assert.Equal("用户改名", (await _profileRepository.GetByIdAsync(profileId))!.Name);
        WriteFile(Path.Combine(mod.SourcePath, "1234567890abcdef.patch_0"), "new content");
        var changed = await _migration.MigrateAsync(plan);
        Assert.Equal(1, changed.ImportedMods);
        Assert.Equal(1, changed.ImportedProfiles);
        Assert.NotEqual(first.ModIds["same"], changed.ModIds["same"]);
        Assert.Equal(3, (await _modRepository.GetAllAsync()).Count());
        Assert.Equal("original", File.ReadAllText(Path.Combine((await _modRepository.GetByIdAsync(userMod))!.Path, "1234567890abcdef.patch_0")));
    }

    [Fact]
    public async Task Migrate_ReverseDirection_RemapsGroupsAndPreservesDeploymentPriority()
    {
        var plan = Plan(SourceMod("a"), SourceMod("b"), SourceMod("c"));
        var source = new ExternalMigrationProfile { SourceId = "p", Name = "顺序", DeploymentOrder = DeploymentOrder.BottomToTop };
        source.Mods.AddRange(plan.Profiles[0].Mods);
        source.Separators.Add(new("前组", 0, true, "#112233", 2));
        source.Separators.Add(new("后组", 2, false, "#445566", 1));
        plan.Profiles.Clear();
        plan.Profiles.Add(source);
        var result = await _migration.MigrateAsync(plan);
        var profile = (await _profileRepository.GetByIdAsync(result.ProfileIds["p"]))!;
        Assert.Equal(new[] { result.ModIds["c"], result.ModIds["b"], result.ModIds["a"] }, profile.Mods.Select(item => item.ModId));
        Assert.Equal(new[] { "后组", "前组" }, profile.Separators.Select(item => item.Name));
        Assert.Equal(new[] { 0, 1 }, profile.Separators.Select(item => item.Order));
        Assert.Equal(new[] { 1, 2 }, profile.Separators.Select(item => item.MemberCount!.Value));
        Assert.Equal(DeploymentOrder.TopToBottom.ToString(), await _settings.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString()));
    }

    [Fact]
    public async Task Migrate_CancelAfterCommittedMod_RetryReusesSuccessAndCleansCopies()
    {
        var plan = Plan(SourceMod("a"), SourceMod("b"));
        using var cts = new CancellationTokenSource();
        var progress = new ImmediateProgress(item => { if (item.Completed == 1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _migration.MigrateAsync(plan, progress, cts.Token));
        Assert.Single(await _modRepository.GetAllAsync());
        Assert.Single(Directory.GetDirectories(Library), path => Path.GetFileName(path) != "thumbnails");
        var retry = await _migration.MigrateAsync(plan);
        Assert.True(retry.IsComplete);
        Assert.Equal(1, retry.ImportedMods);
        Assert.Equal(1, retry.ReusedMods);
        Assert.Equal(1, retry.ImportedProfiles);
    }

    [Fact]
    public async Task Migrate_DatabaseFailure_LeavesNoIncompleteRecordAndCanRetry()
    {
        using var command = (await _db.GetConnectionAsync()).CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_migration BEFORE INSERT ON Mods BEGIN SELECT RAISE(ABORT,'test write failure'); END";
        await command.ExecuteNonQueryAsync();
        var plan = Plan(SourceMod("failure"));
        var progress = new List<ExternalMigrationProgress>();
        var failed = await _migration.MigrateAsync(plan, new ImmediateProgress(progress.Add));
        Assert.False(failed.IsComplete);
        Assert.Empty(await _modRepository.GetAllAsync());
        Assert.Empty(failed.ProfileIds);
        Assert.DoesNotContain(Directory.GetDirectories(Library), path => Path.GetFileName(path) != "thumbnails");
        Assert.Equal(2, progress.Last().Completed);
        command.CommandText = "DROP TRIGGER reject_migration";
        await command.ExecuteNonQueryAsync();
        Assert.True((await _migration.MigrateAsync(plan)).IsComplete);
    }

    [Fact]
    public async Task Migrate_MissingAndEscapingFiles_ReportsFailureWithoutEmptyProfile()
    {
        var missing = SourceMod("missing");
        Directory.Delete(missing.SourcePath, true);
        var invalid = SourceMod("invalid");
        invalid.FileLayout.Add(new("../outside.patch_0", "selected/file.patch_0"));
        var result = await _migration.MigrateAsync(Plan(missing, invalid));
        Assert.False(result.IsComplete);
        Assert.Empty(result.ModIds);
        Assert.Empty(result.ProfileIds);
        Assert.Equal(3, result.Issues.Count);
        Assert.DoesNotContain(Directory.GetDirectories(Library), path => Path.GetFileName(path) != "thumbnails");
    }

    [Fact]
    public async Task Echelon_NoOptions_DeploysAllDeclaredSets()
    {
        var source = Path.Combine(_root, "echelon-no-options");
        WriteFile(Path.Combine(source, "mods", "m", "1234567890abcdef.patch_0"), "first");
        WriteFile(Path.Combine(source, "mods", "m", "abcdef1234567890.patch_0"), "second");
        WriteJson(Path.Combine(source, "state.json"), new
        {
            version = 2, library = new { m = new { id = "m", store = "m", name = "基础模组", sets = new[] {
                new { files = new Dictionary<string, string> { [""] = "1234567890abcdef.patch_0" } },
                new { files = new Dictionary<string, string> { [""] = "abcdef1234567890.patch_0" } } } } },
            profiles = new[] { new { id = "p", name = "全部集合", entries = new[] { new { type = "mod", id = "m", enabled = true } } } }
        });
        var result = await _migration.MigrateAsync(await _migration.PreviewAsync(new(ExternalManagerKind.Echelon, source)));
        Assert.True(result.IsComplete);
        var game = Path.Combine(_root, "all-sets-game");
        Directory.CreateDirectory(game);
        var deployment = new DeploymentService(_profileRepository, _modRepository, new DeploymentRepository(_db), [new CopyDeploymentMethod()]);
        var deployed = await deployment.DeployProfileAsync(result.ProfileIds["p"], DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);
        Assert.Equal(DeploymentStatus.Completed, deployed.Status);
        Assert.Equal(new[] { "first", "second" }, Directory.GetFiles(Path.Combine(game, "data"), "*.patch_0").Select(File.ReadAllText).Order());
    }

    [Fact]
    public async Task Echelon_PartialOptions_UsesDeclaredDefaultSelections()
    {
        var source = Path.Combine(_root, "echelon-defaults");
        Directory.CreateDirectory(source);
        WriteFile(Path.Combine(source, "state.json"), """
            {"version":2,"library":{"m":{"id":"m","store":"m","name":"状态","sets":[{"files":{"":"first.patch_0"}},{"files":{"":"second.patch_0"}}],
            "options":[{"name":"颜色","include":[],"subs":[{"name":"红","include":[0]},{"name":"蓝","include":[1]}]}],
            "option_defaults":{"on":[false],"sel":[1]}}},"profiles":[{"id":"p","name":"状态","entries":[{"type":"mod","id":"m","options":{"on":[true]}}]}]}
            """);
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.Echelon, source));
        var state = Assert.Single(Assert.Single(plan.Profiles).Mods).Options;
        Assert.Equal(new[] { true }, state.OptionToggles);
        Assert.Equal(new[] { 1 }, state.OptionSelections);
    }

    [Fact]
    public async Task Migrate_ImagesAndPackageMetadata_AreCopiedBeforeSave()
    {
        var external = SourceMod("metadata");
        WriteFile(Path.Combine(external.SourcePath, "manifest.json"), """{"Version":1,"Name":"Original","provenance":{"requires":["BingusSharedLoader"]},"extra":{"test":true}}""");
        var image = Path.Combine(_root, "external-images", "icon.png");
        WriteFile(image, "image data");
        var mod = new ExternalMigrationMod { SourceId = external.SourceId, SourcePath = external.SourcePath, Manifest = external.Manifest, Tags = ["盔甲"], ImagePath = image };
        var result = await _migration.MigrateAsync(Plan(mod));
        Assert.True(result.IsComplete);
        var imported = (await _modRepository.GetByIdAsync(result.ModIds["metadata"]))!;
        Assert.StartsWith(imported.Path, imported.ThumbnailPath);
        Assert.Equal("image data", File.ReadAllText(imported.ThumbnailPath));
        using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(imported.Path, "manifest.json")));
        Assert.True(original.RootElement.GetProperty("extra").GetProperty("test").GetBoolean());
        Assert.Equal("BingusSharedLoader", original.RootElement.GetProperty("provenance").GetProperty("requires")[0].GetString());
        Assert.Contains("盔甲", imported.Tags);
        Assert.Equal(result.ModIds["metadata"], (await _migration.MigrateAsync(Plan(mod))).ModIds["metadata"]);
    }

    [Fact]
    public async Task Migrate_ProfileWriteFailure_RollsBackMembersAndRetriesCommittedMod()
    {
        await _profiles.InitializeAsync();
        using var command = (await _db.GetConnectionAsync()).CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_profile_member BEFORE INSERT ON ProfileMods BEGIN SELECT RAISE(ABORT,'test profile failure'); END";
        await command.ExecuteNonQueryAsync();
        var plan = Plan(SourceMod("profile-write"));
        var failed = await _migration.MigrateAsync(plan);
        Assert.Equal(1, failed.ImportedMods);
        Assert.Empty(failed.ProfileIds);
        Assert.Single(await _profileRepository.GetAllAsync());
        command.CommandText = "DROP TRIGGER reject_profile_member";
        await command.ExecuteNonQueryAsync();
        var retry = await _migration.MigrateAsync(plan);
        Assert.True(retry.IsComplete);
        Assert.Equal(1, retry.ReusedMods);
        Assert.Equal(1, retry.ImportedProfiles);
    }

    [Theory]
    [InlineData(ManifestVersion.Legacy)]
    [InlineData(ManifestVersion.V1)]
    public void Manifest_RoundTrip_PreservesVersionAndExplicitEmptyIncludes(ManifestVersion version)
    {
        var manifest = new ModManifest { Guid = Guid.NewGuid(), Name = "清单", Version = "3.2.1", ManifestVersion = version, Tags = ["盔甲"], LegacyOptions = ["Low", "High"],
            Options = [new() { Name = "空选项", Include = [], SubOptions = [new() { Name = "空子项", Include = [] }] }, new() { Name = "无映射", Include = null }] };
        var path = Path.Combine(_root, "manifest-roundtrip.json");
        var parser = new ManifestParser();
        parser.SaveToFile(manifest, path);
        var parsed = parser.ParseFromFile(path)!;
        Assert.Equal(version, parsed.ManifestVersion);
        Assert.Equal("3.2.1", parsed.Version);
        Assert.Equal(manifest.Tags, parsed.Tags);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(version == ManifestVersion.V1 ? 1 : 0, document.RootElement.EnumerateObject().Count(item => item.Name == "Version"));
        if (version == ManifestVersion.Legacy) Assert.Equal(manifest.LegacyOptions, parsed.LegacyOptions);
        else
        {
            Assert.Empty(parsed.Options![0].Include!);
            Assert.Empty(parsed.Options[0].SubOptions![0].Include!);
            Assert.Null(parsed.Options[1].Include);
        }
    }

    private ExternalMigrationMod SourceMod(string id, Guid? guid = null, bool legacy = false)
    {
        var path = Path.Combine(_root, "source-mods", id);
        WriteFile(Path.Combine(path, "1234567890abcdef.patch_0"), "original");
        return new ExternalMigrationMod
        {
            SourceId = id, SourcePath = path,
            Manifest = new ModManifest { Guid = guid ?? Guid.NewGuid(), Name = id, Version = "1.2", ManifestVersion = legacy ? ManifestVersion.Legacy : ManifestVersion.V1,
                LegacyOptions = legacy ? ["Low", "High"] : null }
        };
    }

    private ExternalMigrationPlan Plan(params ExternalMigrationMod[] mods)
    {
        var plan = new ExternalMigrationPlan { Source = new(ExternalManagerKind.HD2Arsenal, _root), SourceIdentity = _root };
        plan.Mods.AddRange(mods);
        var profile = new ExternalMigrationProfile { SourceId = "p", Name = "迁移配置" };
        profile.Mods.AddRange(mods.Select(mod => new ExternalMigrationProfileMod(mod.SourceId, true, new())));
        plan.Profiles.Add(profile);
        return plan;
    }

    private static void WriteJson(string path, object value) => WriteFile(path, JsonSerializer.Serialize(value));
    private static void WriteFile(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
    private sealed class ImmediateProgress(Action<ExternalMigrationProgress> action) : IProgress<ExternalMigrationProgress>
    {
        public void Report(ExternalMigrationProgress value) => action(value);
    }
}
