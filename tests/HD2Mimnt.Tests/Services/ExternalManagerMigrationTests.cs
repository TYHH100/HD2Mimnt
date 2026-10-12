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
    private ExternalManagerExportService _export = null!;
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
        _export = new ExternalManagerExportService([new Helldivers2ModManagerWriter(), new EchelonWriter(), new Hd2ArsenalWriter()], _mods, _profiles, _settings);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HD2MM_DefaultCachedDeletedReferences_FiltersOnlyUnsavedReferences(bool savedMissing, bool unreadableManifest)
    {
        const string defaultId = "00000000-0000-0000-0000-000000000001";
        var source = Path.Combine(_root, "hd2mm-stale");
        var definitions = new[] { SourceMod("first"), SourceMod("second") };
        definitions[0].Manifest.Options = [new() { Name = "颜色", Include = [], SubOptions = [new() { Name = "红", Include = [] }, new() { Name = "蓝", Include = [] }] }];
        foreach (var mod in definitions)
        {
            var path = Path.Combine(source, "Mods", mod.Manifest.Guid.ToString());
            WriteFile(Path.Combine(path, "1234567890abcdef.patch_0"), "data");
            new ManifestParser().SaveToFile(mod.Manifest, Path.Combine(path, "manifest.json"));
        }
        var ids = definitions.Select(mod => mod.Manifest.Guid.ToString()).ToArray();
        var stale = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid().ToString()).ToArray();
        if (unreadableManifest) WriteFile(Path.Combine(source, "Mods", stale[0], "manifest.json"), "{broken");
        var hasMissingMember = savedMissing || unreadableManifest;
        WriteJson(Path.Combine(source, "settings.json"), new { StorageDirectory = source,
            Separators = new[] { new { name = "装备", displayIndex = 0, isExpanded = false, color = "#123456", modGuids = ids.Concat(stale).ToArray() } } });
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(source, "mod_manager.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE mod_groups(Id TEXT,Name TEXT,DisplayIndex INT,ModGuids TEXT);
                CREATE TABLE enabled_mods(Guid TEXT,Enabled INT,Toggled TEXT,Selected TEXT,SortOrder INT);
                CREATE TABLE group_enabled_mods(GroupId TEXT,Guid TEXT,Enabled INT,Toggled TEXT,Selected TEXT,SortOrder INT);
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO mod_groups VALUES(@group,'默认配置文件',0,@members)";
            command.Parameters.AddWithValue("@group", defaultId);
            command.Parameters.AddWithValue("@members", JsonSerializer.Serialize(ids.Concat(stale)));
            await command.ExecuteNonQueryAsync();
            foreach (var (id, index) in ids.Select((id, index) => (id, index)).Concat(savedMissing ? new[] { (stale[0], 2) } : []))
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO group_enabled_mods VALUES(@group,@id,@on,'[false]','[1]',@order); INSERT INTO enabled_mods VALUES(@id,@on,'[false]','[1]',@order)";
                command.Parameters.AddWithValue("@group", defaultId);
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@on", index == 0);
                command.Parameters.AddWithValue("@order", index);
                await command.ExecuteNonQueryAsync();
            }
        }
        await _profiles.InitializeAsync();
        var targetId = (await _profiles.GetActiveProfileAsync())!.Id;
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.Helldivers2ModManager, source, source));
        var sourceProfile = Assert.Single(plan.Profiles);
        Assert.True(sourceProfile.IsDefault);
        Assert.Equal(hasMissingMember ? 3 : 2, sourceProfile.Mods.Count);
        Assert.Contains(plan.Issues, issue => issue.Message.Contains($"已忽略 {(hasMissingMember ? 9 : 10)} 个"));
        var result = await _migration.MigrateAsync(plan);
        Assert.Equal(2, result.ImportedMods);
        var target = Assert.Single(await _profileRepository.GetAllAsync());
        Assert.Equal(targetId, target.Id);
        Assert.Equal("Default Profiles", target.Name);
        if (hasMissingMember)
        {
            Assert.Empty(result.ProfileIds);
            Assert.Empty(target.Mods);
            Assert.Empty(target.DefaultMigrationState);
            Assert.Contains(result.Issues, issue => issue.Message.Contains("未成功迁入"));
            return;
        }
        Assert.Equal(targetId, result.ProfileIds[defaultId]);
        Assert.Equal(ids.Select(id => result.ModIds[id]), target.Mods.Select(item => item.ModId));
        Assert.True(target.Mods[0].Enabled);
        Assert.False(target.Mods[1].Enabled);
        Assert.Equal(new[] { false }, ProfileModOptionsState.FromJson(target.Mods[0].OptionsJson).OptionToggles);
        Assert.Equal(new[] { 1 }, ProfileModOptionsState.FromJson(target.Mods[0].OptionsJson).OptionSelections);
        Assert.Equal(("装备", 0, (int?)2, true, "#123456"),
            (target.Separators[0].Name, target.Separators[0].Order, target.Separators[0].MemberCount, target.Separators[0].IsCollapsed, target.Separators[0].Color));
        var retry = await _migration.MigrateAsync(plan);
        Assert.Equal(2, retry.ReusedMods);
        Assert.Equal(1, retry.ReusedProfiles);
        Assert.Single((await _profileRepository.GetByIdAsync(targetId))!.Separators);
    }

    [Theory]
    [InlineData(ExternalManagerKind.HD2Arsenal)]
    [InlineData(ExternalManagerKind.Echelon)]
    public async Task NativeDefault_MapsToExistingDefault_ActiveNonDefaultRemainsIndependent(ExternalManagerKind manager)
    {
        var source = Path.Combine(_root, "native-default");
        if (manager == ExternalManagerKind.HD2Arsenal)
            WriteJson(Path.Combine(source, "hd2a_data.json"), new { modsLibrary = Array.Empty<object>(), selectedProfile = "custom", profileOrder = new[] { "custom", "default" },
                modsList = new Dictionary<string, object> { ["custom"] = new { label = "自定义", mods = Array.Empty<object>() }, ["default"] = new { label = "已改名的默认", mods = Array.Empty<object>() } } });
        else
            WriteJson(Path.Combine(source, "state.json"), new { version = 2, library = new { }, active_profile = "custom",
                profiles = new[] { new { id = "custom", name = "自定义", entries = Array.Empty<object>() }, new { id = "random-native-id", name = "Default", entries = Array.Empty<object>() } } });
        await _profiles.InitializeAsync();
        var defaultId = (await _profiles.GetActiveProfileAsync())!.Id;
        var plan = await _migration.PreviewAsync(new(manager, source));
        var nativeDefault = Assert.Single(plan.Profiles, profile => profile.IsDefault);
        Assert.False(plan.Profiles[0].IsDefault);
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete);
        Assert.Equal(defaultId, result.ProfileIds[nativeDefault.SourceId]);
        Assert.NotEqual(defaultId, result.ProfileIds["custom"]);
        Assert.Equal(2, (await _profileRepository.GetAllAsync()).Count());
        Assert.Equal(defaultId, (await _profiles.GetActiveProfileAsync())!.Id);
    }

    [Fact]
    public async Task Echelon_DuplicateDefaultNames_DoesNotGuessDefaultIdentity()
    {
        var source = Path.Combine(_root, "echelon-ambiguous");
        WriteJson(Path.Combine(source, "state.json"), new { version = 2, library = new { }, active_profile = "one",
            profiles = new[] { new { id = "one", name = "Default", entries = Array.Empty<object>() }, new { id = "two", name = "Default", entries = Array.Empty<object>() } } });
        var plan = await _migration.PreviewAsync(new(ExternalManagerKind.Echelon, source));
        Assert.All(plan.Profiles, profile => Assert.False(profile.IsDefault));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrate_DefaultMerge_PreservesExistingContentAndRetriesAtomically(bool writeFailure)
    {
        var seed = Plan(SourceMod("existing"));
        seed.Profiles.Clear();
        var existingId = (await _migration.MigrateAsync(seed)).ModIds["existing"];
        await _profiles.InitializeAsync();
        var target = (await _profiles.GetActiveProfileAsync())!;
        target.Mods.Add(new() { ProfileId = target.Id, ModId = existingId, Enabled = false, Order = 0 });
        var oldSeparatorId = Guid.NewGuid();
        target.Separators.Add(new() { Id = oldSeparatorId, ProfileId = target.Id, Name = "已有分组", Order = 0, MemberCount = 1, Color = "#ABCDEF", IsCollapsed = true });
        await _profileRepository.UpdateAsync(target);
        var activeId = await _profiles.CreateProfileAsync("当前配置");
        await _profiles.SetActiveProfileAsync(activeId);
        var plan = Plan(SourceMod("a"), SourceMod("b"));
        var incoming = new ExternalMigrationProfile { SourceId = "default", Name = "源默认", IsDefault = true, WasActive = true };
        incoming.Mods.Add(new("b", false, new()));
        incoming.Mods.Add(new("a", true, new()));
        incoming.Separators.Add(new("迁入分组", 0, true, "#123456", 2));
        plan.Profiles.Clear(); plan.Profiles.Add(incoming);
        using var command = (await _db.GetConnectionAsync()).CreateCommand();
        if (writeFailure)
        {
            command.CommandText = "CREATE TRIGGER reject_default_separator BEFORE INSERT ON ProfileSeparators WHEN NEW.Name='迁入分组' BEGIN SELECT RAISE(ABORT,'test failure'); END";
            await command.ExecuteNonQueryAsync();
            var failed = await _migration.MigrateAsync(plan);
            Assert.Empty(failed.ProfileIds);
            var unchanged = (await _profileRepository.GetByIdAsync(target.Id))!;
            Assert.Equal(existingId, Assert.Single(unchanged.Mods).ModId);
            Assert.Equal(oldSeparatorId, Assert.Single(unchanged.Separators).Id);
            Assert.Empty(unchanged.DefaultMigrationState);
            command.CommandText = "DROP TRIGGER reject_default_separator";
            await command.ExecuteNonQueryAsync();
        }
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        Assert.Equal(target.Id, result.ProfileIds["default"]);
        var merged = (await _profileRepository.GetByIdAsync(target.Id))!;
        Assert.Equal(new[] { existingId, result.ModIds["b"], result.ModIds["a"] }, merged.Mods.Select(item => item.ModId));
        Assert.Equal(new[] { false, false, true }, merged.Mods.Select(item => item.Enabled));
        Assert.Equal((oldSeparatorId, 0, (int?)1, "#ABCDEF", true),
            (merged.Separators[0].Id, merged.Separators[0].Order, merged.Separators[0].MemberCount, merged.Separators[0].Color, merged.Separators[0].IsCollapsed));
        Assert.Equal((1, (int?)2), (merged.Separators[1].Order, merged.Separators[1].MemberCount));
        Assert.True(merged.IsDefault); Assert.False(merged.IsActive); Assert.Equal(target.Name, merged.Name);
        Assert.Equal(activeId, (await _profiles.GetActiveProfileAsync())!.Id);
        merged.Mods.Last().Enabled = false;
        await _profileRepository.UpdateAsync(merged);
        var retry = await _migration.MigrateAsync(plan);
        Assert.Equal(1, retry.ReusedProfiles);
        Assert.False((await _profileRepository.GetByIdAsync(target.Id))!.Mods.Last().Enabled);
        incoming.Mods.Reverse();
        incoming.Separators[0] = new("源分组更新", 0, false, "#654321", 1);
        var update = await _migration.MigrateAsync(plan);
        Assert.Equal(1, update.ImportedProfiles);
        var changed = (await _profileRepository.GetByIdAsync(target.Id))!;
        Assert.Equal(new[] { existingId, result.ModIds["a"], result.ModIds["b"] }, changed.Mods.Select(item => item.ModId));
        Assert.Equal(2, changed.Separators.Count);
        Assert.Equal("源分组更新", changed.Separators.Last().Name);
        Assert.Equal(oldSeparatorId, changed.Separators[0].Id);
        Assert.Equal(2, Assert.Single(changed.DefaultMigrationState).Value.SnapshotIds.Count);
        WriteFile(Path.Combine(plan.Mods[0].SourcePath, "1234567890abcdef.patch_0"), "new source version");
        var versionUpdate = await _migration.MigrateAsync(plan);
        Assert.True(versionUpdate.IsComplete);
        Assert.NotEqual(result.ModIds["a"], versionUpdate.ModIds["a"]);
        var latest = (await _profileRepository.GetByIdAsync(target.Id))!;
        Assert.Equal(new[] { existingId, versionUpdate.ModIds["a"], result.ModIds["b"] }, latest.Mods.Select(item => item.ModId));
        Assert.Equal(2, latest.Separators.Count);
        incoming.Mods.RemoveAt(1);
        var memberUpdate = await _migration.MigrateAsync(plan);
        Assert.True(memberUpdate.IsComplete);
        Assert.Equal(new[] { existingId, versionUpdate.ModIds["a"] }, (await _profileRepository.GetByIdAsync(target.Id))!.Mods.Select(item => item.ModId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrate_DefaultMerge_OverlapAndLoaders_PreservesRemainingGroupAndDeploymentDirection(bool reverse)
    {
        var a = SourceMod("shared"); var local = SourceMod("local");
        var mdl = SourceMod("mdl", Guid.Parse("761188b2-c45b-4607-9241-f89ca221c47b"));
        var seed = Plan(a, local, mdl); seed.Profiles.Clear();
        var seeded = await _migration.MigrateAsync(seed);
        var target = (await _profiles.GetActiveProfileAsync())!;
        target.Mods = new[] { a, local, mdl }.Select((mod, index) => new ProfileMod
            { ProfileId = target.Id, ModId = seeded.ModIds[mod.SourceId], Enabled = true, Order = index }).ToList();
        target.Separators.Add(new() { Id = Guid.NewGuid(), ProfileId = target.Id, Name = "已有", Order = 0, MemberCount = 2 });
        await _profileRepository.UpdateAsync(target);
        await _settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, reverse ? DeploymentOrder.BottomToTop.ToString() : DeploymentOrder.TopToBottom.ToString());
        var b = SourceMod("new"); var bsl = SourceMod("bsl", Guid.Parse("612eaf70-d682-43c7-9efd-16dcc695f977"));
        var plan = Plan(b, a, bsl); plan.Profiles.Clear();
        var incoming = new ExternalMigrationProfile { SourceId = "default", Name = "默认", IsDefault = true, DeploymentOrder = DeploymentOrder.TopToBottom };
        incoming.Mods.AddRange(new[] { b, a, bsl }.Select(mod => new ExternalMigrationProfileMod(mod.SourceId, true, new())));
        incoming.Separators.Add(new("迁入", 0, false, "#123456", 2));
        plan.Profiles.Add(incoming);
        var result = await _migration.MigrateAsync(plan);
        Assert.True(result.IsComplete, string.Join(";", result.Issues));
        Assert.Equal(seeded.ModIds["shared"], result.ModIds["shared"]);
        var merged = (await _profileRepository.GetByIdAsync(target.Id))!;
        Assert.Equal(5, merged.Mods.Count);
        Assert.Equal(new[] { seeded.ModIds["local"] }.Concat((reverse ? new[] { a, b } : new[] { b, a }).Select(mod => result.ModIds[mod.SourceId])),
            merged.Mods.Take(3).Select(item => item.ModId));
        Assert.Equal(new[] { ("已有", 0, (int?)1), ("迁入", 1, (int?)2) },
            merged.Separators.Select(separator => (separator.Name, separator.Order, separator.MemberCount)));
        Assert.False(merged.Mods.Single(item => item.ModId == seeded.ModIds["mdl"]).Enabled);
        Assert.True(merged.Mods.Single(item => item.ModId == result.ModIds["bsl"]).Enabled);
    }

    [Theory]
    [InlineData(ExternalManagerKind.Helldivers2ModManager, false)]
    [InlineData(ExternalManagerKind.Helldivers2ModManager, true)]
    [InlineData(ExternalManagerKind.Echelon, false)]
    [InlineData(ExternalManagerKind.Echelon, true)]
    [InlineData(ExternalManagerKind.HD2Arsenal, false)]
    [InlineData(ExternalManagerKind.HD2Arsenal, true)]
    public async Task Export_AllManagers_RoundTripPreservesProfilesOptionsGroupsAndPriority(ExternalManagerKind manager, bool reverse)
    {
        var a = SourceMod("a"); var b = SourceMod("b"); var legacy = SourceMod("legacy", legacy: true);
        var loader = SourceMod("loader", Guid.Parse("612eaf70-d682-43c7-9efd-16dcc695f977"));
        a.Manifest.Options = [new() { Name = "配件", Include = [], SubOptions = [new() { Name = "红", Include = ["red"] }, new() { Name = "蓝", Include = ["blue"] }] }];
        WriteFile(Path.Combine(a.SourcePath, "red", "1234567890abcde1.patch_0"), "red");
        WriteFile(Path.Combine(a.SourcePath, "blue", "1234567890abcde2.patch_0"), "blue");
        WriteFile(Path.Combine(a.SourcePath, "blue", "unused", "1234567890abcde5.patch_0"), "unselected nested variant");
        WriteFile(Path.Combine(b.SourcePath, "unused", "1234567890abcde6.patch_0"), "unused files in mod without options");
        WriteFile(Path.Combine(b.SourcePath, "1234567890abcdef.patch_2"), "two");
        WriteFile(Path.Combine(b.SourcePath, "1234567890abcdef.patch_10"), "ten");
        WriteFile(Path.Combine(legacy.SourcePath, "Low", "1234567890abcde3.patch_0"), "low");
        WriteFile(Path.Combine(legacy.SourcePath, "High", "1234567890abcde4.patch_0"), "high");
        var plan = Plan(a, b, legacy, loader);
        var profile = plan.Profiles.Single();
        profile.Separators.Add(new("武器", 0, true, "#123456", 2));
        profile.Mods[0] = new("a", true, new() { OptionToggles = [true], OptionSelections = [1] });
        profile.Mods[1] = new("b", false, new());
        profile.Mods[2] = new("legacy", true, new() { LegacySelectedIndex = 1 });
        var second = new ExternalMigrationProfile { SourceId = "two", Name = "备用" };
        second.Mods.Add(new("a", false, new() { OptionToggles = [false], OptionSelections = [0] }));
        second.Mods.Add(new("b", true, new())); plan.Profiles.Add(second);
        var imported = await _migration.MigrateAsync(plan);
        await _profiles.SetActiveProfileAsync(imported.ProfileIds["p"]);
        var target = new ExternalMigrationSource(manager, Path.Combine(_root, "external", manager.ToString()), Path.Combine(_root, "install"));
        var nativeFile = Path.Combine(target.DataPath, manager == ExternalManagerKind.Echelon ? "state.json" : "hd2a_data.json");
        if (manager == ExternalManagerKind.Helldivers2ModManager) WriteJson(Path.Combine(target.InstallationPath!, "settings.json"), new { DeployBottomToTop = reverse, theme = "keep" });
        else if (manager == ExternalManagerKind.Echelon) WriteJson(nativeFile, new { version = 2, library = new { }, profiles = Array.Empty<object>(), settings = new { priority = reverse ? "first" : "last", theme = "keep" } });
        else WriteJson(nativeFile, new { modsLibrary = Array.Empty<object>(), modsList = new { }, profileOrder = Array.Empty<string>(), setTopPriority = reverse, theme = "keep" });
        var preview = await _export.PreviewAsync(target);
        Assert.DoesNotContain(preview.Issues, issue => issue.IsBlocking);
        var result = await _export.MigrateAsync(preview);
        Assert.Equal(4, result.ImportedMods); Assert.Equal(preview.Profiles.Count, result.ImportedProfiles);
        Assert.NotEmpty(result.BackupPaths);
        var back = await _migration.PreviewAsync(target);
        Assert.Empty(back.Issues);
        Assert.Equal(4, back.Mods.Count); Assert.Equal(preview.Profiles.Count, back.Profiles.Count);
        var current = back.Profiles.Single(item => item.SourceId == result.ProfileIds[imported.ProfileIds["p"].ToString()].ToString());
        Assert.True(current.WasActive);
        var aId = result.ModIds[imported.ModIds["a"].ToString()].ToString();
        var bId = result.ModIds[imported.ModIds["b"].ToString()].ToString();
        var legacyId = result.ModIds[imported.ModIds["legacy"].ToString()].ToString();
        var aState = current.Mods.Single(item => item.SourceModId == aId);
        Assert.True(aState.Enabled); Assert.True(aState.Options.OptionToggles![0]); Assert.Equal(1, aState.Options.OptionSelections![0]);
        Assert.False(current.Mods.Single(item => item.SourceModId == bId).Enabled);
        Assert.Equal(1, manager == ExternalManagerKind.Helldivers2ModManager ? current.Mods.Single(item => item.SourceModId == legacyId).Options.LegacySelectedIndex
            : current.Mods.Single(item => item.SourceModId == legacyId).Options.OptionSelections![0]);
        var group = current.Separators.Single(separator => separator.Name == "武器");
        Assert.True(group.IsCollapsed); Assert.Equal("#123456", group.Color); Assert.Equal(2, group.MemberCount);
        Assert.Equal(new[] { aId, bId }.Order(), current.Mods.Skip(group.Order).Take(group.MemberCount!.Value).Select(member => member.SourceModId).Order());
        var order = current.Mods.Where(member => member.SourceModId == aId || member.SourceModId == bId || member.SourceModId == legacyId).Select(member => member.SourceModId).ToList();
        if (reverse) order.Reverse(); Assert.Equal(new[] { aId, bId, legacyId }, order);
        var repeated = await _export.MigrateAsync(await _export.PreviewAsync(target));
        Assert.Equal(0, repeated.ImportedMods); Assert.Equal(4, repeated.ReusedMods); Assert.Equal(0, repeated.ImportedProfiles);
        Assert.Equal(preview.Profiles.Count, (await _migration.PreviewAsync(target)).Profiles.Count);

        var roundTrip = await _migration.MigrateAsync(back);
        var game = Path.Combine(_root, "roundtrip-game");
        Directory.CreateDirectory(game);
        var deployment = new DeploymentService(_profileRepository, _modRepository, new DeploymentRepository(_db), [new CopyDeploymentMethod()]);
        var deployed = await deployment.DeployProfileAsync(roundTrip.ProfileIds[current.SourceId], DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);
        Assert.Equal(DeploymentStatus.Completed, deployed.Status);
        Assert.Equal(new[] { "blue", "high", "original" }, Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*")
            .Where(path => !path.EndsWith(".stream") && !path.EndsWith(".gpu_resources")).Select(File.ReadAllText).Order());
        var other = back.Profiles.Single(item => item.SourceId == result.ProfileIds[imported.ProfileIds["two"].ToString()].ToString());
        var otherGame = Path.Combine(_root, "other-roundtrip-game");
        Directory.CreateDirectory(otherGame);
        Assert.Equal(DeploymentStatus.Completed, (await deployment.DeployProfileAsync(roundTrip.ProfileIds[other.SourceId], DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, otherGame)).Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(otherGame, "data", "1234567890abcdef.patch_0")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(otherGame, "data", "1234567890abcdef.patch_1")));
        Assert.Equal("ten", File.ReadAllText(Path.Combine(otherGame, "data", "1234567890abcdef.patch_2")));
    }

    [Fact]
    public async Task Export_Arsenal_RestoresLibrarySeparatorAndKeepsExistingNativeData()
    {
        var a = SourceMod("a"); var b = SourceMod("b"); var plan = Plan(a, b);
        var original = plan.Profiles.Single();
        var layout = new ExternalMigrationProfile { SourceId = "library-layout", Name = "库布局", IsLibraryLayout = true };
        layout.Mods.AddRange(original.Mods.Select(member => member with { Enabled = false })); layout.Separators.Add(new("黄色", 0, true, "#FEE800", 1));
        plan.Profiles.Add(layout);
        var imported = await _migration.MigrateAsync(plan);
        Assert.True((await _profileRepository.GetByIdAsync(imported.ProfileIds["library-layout"]))!.IsLibraryLayout);
        var existing = SourceMod("old");
        var target = new ExternalMigrationSource(ExternalManagerKind.HD2Arsenal, Path.Combine(_root, "arsenal-target"), Path.Combine(_root, "install"));
        var path = Path.Combine(target.DataPath, "hd2a_data.json");
        WriteJson(path, new { modsLibrary = new[] { new { uuid = "old", label = "原模组", path = existing.SourcePath } },
            modsList = new { old = new { label = "原配置", mods = Array.Empty<object>() } }, profileOrder = new[] { "old" }, zoomLevel = 1.2 });
        var before = File.ReadAllText(path);
        var result = await _export.MigrateAsync(await _export.PreviewAsync(target));
        Assert.Contains(result.BackupPaths, backup => File.ReadAllText(backup) == before);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1.2, document.RootElement.GetProperty("zoomLevel").GetDouble());
        Assert.Equal("原配置", document.RootElement.GetProperty("modsList").GetProperty("old").GetProperty("label").GetString());
        var back = await _migration.PreviewAsync(target);
        var libraryLayout = back.Profiles.Single(profile => profile.IsLibraryLayout);
        var separator = libraryLayout.Separators.Single(separator => separator.Name == "黄色");
        Assert.Equal((true, "#FEE800", (int?)1), (separator.IsCollapsed, separator.Color, separator.MemberCount));
    }

    [Fact]
    public async Task Export_TargetChangedAfterPreview_RejectsCommitAndCleansStaging()
    {
        await _migration.MigrateAsync(Plan(SourceMod("a")));
        var target = new ExternalMigrationSource(ExternalManagerKind.HD2Arsenal, Path.Combine(_root, "export"), Path.Combine(_root, "install"));
        var preview = await _export.PreviewAsync(target);
        var path = Path.Combine(target.DataPath, "hd2a_data.json");
        WriteJson(path, new { modsLibrary = Array.Empty<object>(), modsList = new { }, currentUserChange = true });
        var expected = File.ReadAllText(path);
        await Assert.ThrowsAsync<IOException>(() => _export.MigrateAsync(preview));
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Empty(Directory.GetDirectories(target.DataPath, ".hd2mimnt-migration-*"));
        Assert.False(Directory.Exists(Path.Combine(target.DataPath, "mods")));
    }

    [Fact]
    public async Task Export_CancelBeforeCommit_KeepsTargetUnchangedAndCleansStaging()
    {
        await _migration.MigrateAsync(Plan(SourceMod("a")));
        var target = new ExternalMigrationSource(ExternalManagerKind.HD2Arsenal, Path.Combine(_root, "export"), Path.Combine(_root, "install"));
        var preview = await _export.PreviewAsync(target);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value => { if (value.Stage == "保存迁移结果") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _export.MigrateAsync(preview, progress, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(target.DataPath, "hd2a_data.json")));
        Assert.Empty(Directory.GetDirectories(target.DataPath, ".hd2mimnt-migration-*"));
        Assert.False(Directory.Exists(Path.Combine(target.DataPath, "mods")));
        var retried = await _export.MigrateAsync(await _export.PreviewAsync(target));
        Assert.Equal(1, retried.ImportedMods);
    }

    [Fact]
    public async Task Export_SourceChangedAfterPreview_RejectsCommitAndCleansStaging()
    {
        var imported = await _migration.MigrateAsync(Plan(SourceMod("a")));
        var target = new ExternalMigrationSource(ExternalManagerKind.HD2Arsenal, Path.Combine(_root, "export"), Path.Combine(_root, "install"));
        var preview = await _export.PreviewAsync(target);
        var mod = (await _modRepository.GetByIdAsync(imported.ModIds["a"]))!;
        WriteFile(Path.Combine(mod.Path, "1234567890abcdef.patch_0"), "changed after preview");
        await Assert.ThrowsAsync<IOException>(() => _export.MigrateAsync(preview));
        Assert.False(File.Exists(Path.Combine(target.DataPath, "hd2a_data.json")));
        Assert.Empty(Directory.GetDirectories(target.DataPath, ".hd2mimnt-migration-*"));
        Assert.False(Directory.Exists(Path.Combine(target.DataPath, "mods")));
    }

    [Fact]
    public async Task Export_MetadataPublishFails_RollsBackPublishedFilesAndMods()
    {
        await _migration.MigrateAsync(Plan(SourceMod("a")));
        var target = new ExternalMigrationSource(ExternalManagerKind.Echelon, Path.Combine(_root, "export"), Path.Combine(_root, "install"));
        var statePath = Path.Combine(target.DataPath, "state.json");
        var locationsPath = Path.Combine(target.DataPath, "locations.json");
        WriteJson(statePath, new { version = 2, library = new { }, profiles = Array.Empty<object>(), settings = new { priority = "last" } });
        WriteJson(locationsPath, new { mods = Path.Combine(target.DataPath, "mods"), kept = true });
        var stateBefore = File.ReadAllText(statePath); var locationsBefore = File.ReadAllText(locationsPath);
        var preview = await _export.PreviewAsync(target);
        using (var fileLock = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => _export.MigrateAsync(preview));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        Assert.Equal(stateBefore, File.ReadAllText(statePath));
        Assert.Equal(locationsBefore, File.ReadAllText(locationsPath));
        Assert.Empty(Directory.GetDirectories(target.DataPath, ".hd2mimnt-migration-*"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(target.DataPath, "mods")));
        Assert.Empty(Directory.GetFiles(target.DataPath, "*.hd2mimnt-pending-*"));
        Assert.Equal(1, (await _export.MigrateAsync(await _export.PreviewAsync(target))).ImportedMods);
    }

    [Theory]
    [InlineData(ExternalManagerKind.Echelon)]
    [InlineData(ExternalManagerKind.HD2Arsenal)]
    public async Task Export_EmptySeparator_DoesNotHideFollowingMods(ExternalManagerKind manager)
    {
        var plan = Plan(SourceMod("a"), SourceMod("b"));
        plan.Profiles.Single().Separators.Add(new("空组", 0, true, "#123456", 0));
        var imported = await _migration.MigrateAsync(plan);
        var target = new ExternalMigrationSource(manager, Path.Combine(_root, "export"), Path.Combine(_root, "install"));
        var exported = await _export.MigrateAsync(await _export.PreviewAsync(target));
        var back = await _migration.PreviewAsync(target);
        var profile = back.Profiles.Single(profile => profile.SourceId == exported.ProfileIds[imported.ProfileIds["p"].ToString()].ToString());
        Assert.Equal(0, profile.Separators.Single(separator => separator.Name == "空组").MemberCount);
        Assert.Equal(2, profile.Mods.Count);
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
