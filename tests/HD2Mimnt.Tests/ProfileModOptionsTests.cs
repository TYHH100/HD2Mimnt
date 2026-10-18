using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Tests;

/// <summary>
/// 配置文件模组选项（v0 互斥下拉 / v1 独立开关+子选项）：
/// 存储、持久化、清单兼容解析与部署文件过滤。
/// </summary>
public class ProfileModOptionsTests : IDisposable
{
    private readonly string _testDbPath = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_opts_{Guid.NewGuid()}.db");
    private readonly string _modDirRoot = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_opts_mods_{Guid.NewGuid()}");
    private readonly string _gameDirRoot = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_opts_game_{Guid.NewGuid()}");

    private readonly HD2MimntDbContext _dbContext;
    private readonly ModRepository _modRepository;
    private readonly ProfileRepository _profileRepository;
    private readonly ProfileService _profileService;

    public ProfileModOptionsTests()
    {
        Directory.CreateDirectory(_gameDirRoot);
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _profileService = new ProfileService(_profileRepository, _modRepository);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        TryDelete(_testDbPath);
        TryDelete(_modDirRoot, recursive: true);
        TryDelete(_gameDirRoot, recursive: true);
    }

    private static void TryDelete(string path, bool recursive = false)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不影响测试结果
        }
    }

    private async Task<Mod> CreateModAsync(string name, ModManifest? manifest)
    {
        var modDir = Path.Combine(_modDirRoot, name);
        Directory.CreateDirectory(modDir);

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = "1.0",
            Path = modDir,
            ImportedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Manifest = manifest
        };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    [Fact]
    public async Task Conflicts_ShouldFollowSelectedVariantsFlattenedTargetsAndEnabledState()
    {
        var first = await CreateModAsync("First", new ModManifest
        {
            ManifestVersion = ManifestVersion.Legacy, LegacyOptions = new() { "A", "B" }
        });
        var second = await CreateModAsync("Second", new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = new() { new ModOption { Name = "Body", Include = new() { "Selected", "Selected" } } }
        });
        Directory.CreateDirectory(Path.Combine(first.Path, "A"));
        Directory.CreateDirectory(Path.Combine(first.Path, "B"));
        Directory.CreateDirectory(Path.Combine(second.Path, "Selected"));
        Services.ConflictDetectionServiceTests.WritePatch(Path.Combine(first.Path, "A", "0123456789abcdef.patch_0"), 1);
        Services.ConflictDetectionServiceTests.WritePatch(Path.Combine(first.Path, "B", "fedcba9876543210.patch_0"), 2);
        Services.ConflictDetectionServiceTests.WritePatch(Path.Combine(second.Path, "Selected", "9999999999999999.patch_7"), 1);
        File.WriteAllText(Path.Combine(first.Path, "preview.png"), "image");
        File.WriteAllText(Path.Combine(second.Path, "preview.png"), "image");
        var profileId = await _profileService.CreateProfileAsync("检测配置");
        await _profileService.AddModToProfileAsync(profileId, first.Id);
        await _profileService.AddModToProfileAsync(profileId, second.Id);
        var scanner = new ConflictDetectionService(_profileRepository, _modRepository);
        var result = await scanner.ScanProfileAsync(profileId);
        var group = Assert.Single(result.Groups);
        Assert.Equal(1UL, group.ResourceId);
        Assert.Equal(new[] { first.Id, second.Id }, group.Owners.Select(owner => owner.ModId));
        await _profileService.ReorderModsAsync(profileId, new() { second.Id, first.Id });
        Assert.Equal(first.Id, Assert.Single((await scanner.ScanProfileAsync(profileId)).Groups).Owners.Last().ModId);
        await _profileService.SetModOptionsAsync(profileId, first.Id, new() { LegacySelectedIndex = 1 });
        Assert.Empty((await scanner.ScanProfileAsync(profileId)).Groups);
        await _profileService.SetModOptionsAsync(profileId, first.Id, new() { LegacySelectedIndex = 0 });
        await _profileService.SetModOptionsAsync(profileId, second.Id, new() { OptionToggles = new() { false } });
        Assert.Empty((await scanner.ScanProfileAsync(profileId)).Groups);
        await _profileService.SetModOptionsAsync(profileId, second.Id, new() { OptionToggles = new() { true } });
        await _profileService.SetModEnabledAsync(profileId, second.Id, false);
        Assert.Empty((await scanner.ScanProfileAsync(profileId)).Groups);
        await _profileService.SetModEnabledAsync(profileId, second.Id, true);
        await _profileService.SetModOptionsAsync(profileId, first.Id, new() { LegacySelectedIndex = 99 });
        // 与部署共用 ResolveEffective：旧索引越界时回到默认选项 0。
        var fallback = await scanner.ScanProfileAsync(profileId);
        Assert.Single(fallback.Groups);
        Assert.Equal(0, fallback.SkippedModCount);
    }

    // ── ProfileService 持久化 ──

    [Fact]
    public async Task SetModOptions_ShouldPersistState_AndPreserveOrderAndEnabled()
    {
        // Arrange：三个模组按特定顺序排列，中间模组禁用
        var modA = await CreateModAsync("ModA", null);
        var modB = await CreateModAsync("ModB", null);
        var modC = await CreateModAsync("ModC", null);
        var profileId = await _profileService.CreateProfileAsync("P1");
        await _profileService.AddModToProfileAsync(profileId, modA.Id);
        await _profileService.AddModToProfileAsync(profileId, modB.Id);
        await _profileService.AddModToProfileAsync(profileId, modC.Id);
        await _profileService.ReorderModsAsync(profileId, new List<Guid> { modC.Id, modA.Id, modB.Id });
        await _profileService.SetModEnabledAsync(profileId, modA.Id, false);

        // Act：保存选项状态
        var state = new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } };
        await _profileService.SetModOptionsAsync(profileId, modB.Id, state);

        // Assert
        var profile = await _profileService.GetProfileByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Equal(new[] { modC.Id, modA.Id, modB.Id }, profile!.Mods.OrderBy(m => m.Order).Select(m => m.ModId));
        Assert.False(profile.Mods.Single(m => m.ModId == modA.Id).Enabled);

        var saved = ProfileModOptionsState.FromJson(profile.Mods.Single(m => m.ModId == modB.Id).OptionsJson);
        Assert.Equal(new List<bool> { true, false }, saved.OptionToggles);
    }

    [Fact]
    public async Task SetModOptions_RepeatedToggle_ShouldKeepLatestState()
    {
        var mod = await CreateModAsync("ModT", null);
        var profileId = await _profileService.CreateProfileAsync("P1");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // 快速连续两次保存（模拟连续勾选），串行闸门应保证最终状态为最后一次
        await _profileService.SetModOptionsAsync(profileId, mod.Id,
            new ProfileModOptionsState { OptionToggles = new List<bool> { true, true } });
        await _profileService.SetModOptionsAsync(profileId, mod.Id,
            new ProfileModOptionsState { OptionToggles = new List<bool> { false, true } });

        var profile = await _profileService.GetProfileByIdAsync(profileId);
        var saved = ProfileModOptionsState.FromJson(profile!.Mods.Single().OptionsJson);
        Assert.Equal(new List<bool> { false, true }, saved.OptionToggles);
    }

    [Fact]
    public async Task DuplicateProfile_ShouldCopyOptionsState()
    {
        var manifest = new ModManifest
        {
            Name = "M",
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption> { new() { Name = "A" }, new() { Name = "B" } }
        };
        var mod = await CreateModAsync("ModD", manifest);
        var profileId = await _profileService.CreateProfileAsync("P1");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        await _profileService.SetModOptionsAsync(profileId, mod.Id,
            new ProfileModOptionsState { OptionToggles = new List<bool> { false, true } });

        var duplicated = await _profileService.DuplicateProfileAsync(profileId, "P1 Copy");

        var copy = await _profileService.GetProfileByIdAsync(duplicated.Id);
        var saved = ProfileModOptionsState.FromJson(copy!.Mods.Single().OptionsJson);
        Assert.Equal(new List<bool> { false, true }, saved.OptionToggles);
    }

    // ── 软件升级（Schema 迁移）绝不打乱配置文件中的模组顺序 ──

    [Fact]
    public async Task InitializeDatabase_OldSchemaWithoutOptionsJson_ShouldPreserveRowsAndOrder()
    {
        // Arrange：按旧版 Schema（ProfileMods 无 OptionsJson 列）建库并写入特定顺序
        var oldDbPath = Path.Combine(Path.GetTempPath(), $"test_hd2mimnt_migrate_{Guid.NewGuid()}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={oldDbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE Profiles (
                        Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE, Description TEXT,
                        CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, IsActive INTEGER NOT NULL);
                    CREATE TABLE Mods (
                        Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Version TEXT NOT NULL, Path TEXT NOT NULL,
                        Category INTEGER NOT NULL DEFAULT 0, ImportedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL,
                        Source INTEGER NOT NULL DEFAULT 0, Status INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE ProfileMods (
                        ProfileId TEXT NOT NULL, ModId TEXT NOT NULL,
                        Enabled INTEGER NOT NULL, [Order] INTEGER NOT NULL,
                        PRIMARY KEY (ProfileId, ModId));
                    INSERT INTO Profiles (Id, Name, Description, CreatedAt, ModifiedAt, IsActive)
                    VALUES ('11111111-1111-1111-1111-111111111111', 'P', '', '2024-01-01', '2024-01-01', 1);
                    INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt) VALUES
                        ('22222222-2222-2222-2222-222222222222', 'A', '1', 'X', '2024-01-01', '2024-01-01'),
                        ('33333333-3333-3333-3333-333333333333', 'B', '1', 'X', '2024-01-01', '2024-01-01'),
                        ('44444444-4444-4444-4444-444444444444', 'C', '1', 'X', '2024-01-01', '2024-01-01');
                    INSERT INTO ProfileMods (ProfileId, ModId, Enabled, [Order]) VALUES
                        ('11111111-1111-1111-1111-111111111111', '33333333-3333-3333-3333-333333333333', 1, 0),
                        ('11111111-1111-1111-1111-111111111111', '44444444-4444-4444-4444-444444444444', 0, 1),
                        ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 1, 2);";
                command.ExecuteNonQuery();
            }

            // Act：模拟软件升级后在新版本代码中打开同一数据库
            using var upgradedContext = new HD2MimntDbContext(oldDbPath);
            await upgradedContext.InitializeDatabaseAsync();
            var repository = new ProfileRepository(upgradedContext);
            var profile = await repository.GetByIdAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));

            // Assert：行、启用状态与顺序原样保留，新列取默认空串
            Assert.NotNull(profile);
            var ordered = profile!.Mods.OrderBy(m => m.Order).ToList();
            Assert.Equal(3, ordered.Count);
            // Guid 首字符：B=3、C=4、A=2，写入顺序为 B(0)、C(1)、A(2)
            Assert.Equal(new[] { "3", "4", "2" },
                ordered.Select(m => m.ModId.ToString().Substring(0, 1)).ToArray());
            Assert.False(ordered[1].Enabled);
            Assert.All(ordered, m => Assert.Equal(string.Empty, m.OptionsJson));

            // 迁移后写入选项状态也应正常工作
            ordered[0].OptionsJson = new ProfileModOptionsState { LegacySelectedIndex = 1 }.ToJson();
            await repository.UpdateAsync(profile);
            var reloaded = await repository.GetByIdAsync(profile.Id);
            Assert.Equal(1, ProfileModOptionsState.FromJson(reloaded!.Mods.Single(m => m.Order == 0).OptionsJson).LegacySelectedIndex);
        }
        finally
        {
            TryDelete(oldDbPath);
        }
    }

    // ── 选项状态归一化 ──

    [Fact]
    public void ResolveEffective_V1_WithoutStoredState_DefaultsAllEnabled()
    {
        // 旧版清单没有 DefaultEnabled 字段（null），缺省应为“启用”而不是全部停用
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "A" },
                new() { Name = "B", DefaultEnabled = false },
                new() { Name = "C", SubOptions = new List<ModSubOption> { new() { Name = "C1" }, new() { Name = "C2" } } }
            }
        };

        var state = ProfileModOptionResolver.ResolveEffective(manifest, null);

        Assert.Equal(new List<bool> { true, false, true }, state.OptionToggles);
        Assert.Equal(new List<int> { 0, 0, 0 }, state.OptionSelections);
    }

    [Fact]
    public void ResolveEffective_V1_AdaptsStoredState_WhenManifestChanged()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "A" },
                new() { Name = "B" },
                new() { Name = "C", SubOptions = new List<ModSubOption> { new() { Name = "C1" }, new() { Name = "C2" }, new() { Name = "C3" } } }
            }
        };
        // 存的是旧清单的状态：开关数组偏短（缺的补默认启用），子选项索引越界（钳制回有效范围）
        var stored = new ProfileModOptionsState
        {
            OptionToggles = new List<bool> { false },
            OptionSelections = new List<int> { 0, 0, 5 }
        }.ToJson();

        var state = ProfileModOptionResolver.ResolveEffective(manifest, stored);

        Assert.Equal(new List<bool> { false, true, true }, state.OptionToggles);
        Assert.Equal(new List<int> { 0, 0, 2 }, state.OptionSelections);
    }

    [Fact]
    public void ResolveEffective_Legacy_DefaultsToFirst_AndClampsOutOfRange()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "Low", "Mid", "High" }
        };

        Assert.Equal(0, ProfileModOptionResolver.ResolveEffective(manifest, null).LegacySelectedIndex);
        Assert.Equal(2, ProfileModOptionResolver.ResolveEffective(manifest,
            new ProfileModOptionsState { LegacySelectedIndex = 2 }.ToJson()).LegacySelectedIndex);
        Assert.Equal(0, ProfileModOptionResolver.ResolveEffective(manifest,
            new ProfileModOptionsState { LegacySelectedIndex = 9 }.ToJson()).LegacySelectedIndex);
    }

    // ── 部署展开规则 ──

    [Fact]
    public void ResolveDeployPlan_V1_CollectsEnabledOptionsAndSelectedSubOption()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "A", Include = new List<string> { "A1", "A2" } },
                new() { Name = "B", Include = new List<string> { "B1" } },
                new()
                {
                    Name = "C",
                    Include = new List<string>(),
                    SubOptions = new List<ModSubOption>
                    {
                        new() { Name = "C1", Include = new List<string> { "C1a" } },
                        new() { Name = "C2", Include = new List<string> { "C2a", "C2b" } }
                    }
                }
            }
        };
        var state = new ProfileModOptionsState
        {
            OptionToggles = new List<bool> { true, false, true },
            OptionSelections = new List<int> { 0, 0, 1 }
        };

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest, state);

        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Equal(new[] { "A1", "A2", "C2a", "C2b" }, plan.Directories);
    }

    [Fact]
    public void ResolveDeployPlan_V1_MissingInclude_FallsBackToFullDirectory()
    {
        // Include 缺失（清单未声明映射）时回退全量部署，保持旧行为
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "A" },
                new() { Name = "B", Include = new List<string> { "B1" } }
            }
        };

        var plan = ProfileModOptionResolver.ResolveDeployPlan(manifest, new ProfileModOptionsState());

        Assert.Equal(OptionDeployScope.FullDirectory, plan.Scope);
    }

    [Fact]
    public void ResolveDeployPlan_Legacy_InvalidIndex_IsInvalidSelection()
    {
        var manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "Low", "High" }
        };

        // 索引越界时不能回退全量（会把互斥选项一起部署），必须整个模组跳过
        var plan = ProfileModOptionResolver.ResolveDeployPlan(
            manifest, new ProfileModOptionsState { LegacySelectedIndex = 7 });

        Assert.Equal(OptionDeployScope.InvalidSelection, plan.Scope);
    }

    [Fact]
    public async Task ModManifest_GeneratedComponentGroups_RoundTripAndKeepFlatDeployPlan()
    {
        // HD2IAS 导入生成的一级组件分组与扁平选项：分组只用于配置页导航，
        // 选项的 Include 映射与部署规则保持原有语义。
        var manifest = new ModManifest
        {
            Name = "Generated",
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "系统", GroupId = "g0", Include = new List<string> { "rig" } },
                new() { Name = "外观", GroupId = "g1", Include = new List<string> { "A" } }
            },
            OptionGroups = new List<ModOptionGroup>
            {
                new() { Id = "g0", Name = "系统组件" },
                new() { Id = "g1", Name = "外观组件", Image = "preview/cover.png" }
            }
        };
        var mod = await CreateModAsync("Grouped", manifest);

        var loaded = await _modRepository.GetByIdAsync(mod.Id);

        Assert.Equal(new[] { "系统组件", "外观组件" }, loaded!.Manifest!.OptionGroups!.Select(group => group.Name));
        Assert.Equal("preview/cover.png", loaded.Manifest.OptionGroups[1].Image);
        Assert.All(loaded.Manifest.Options!, option => Assert.False(string.IsNullOrWhiteSpace(option.GroupId)));
        var plan = ProfileModOptionResolver.ResolveDeployPlan(loaded.Manifest,
            new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } });
        Assert.Equal(OptionDeployScope.DirectoryList, plan.Scope);
        Assert.Equal(new[] { "rig" }, plan.Directories);
    }

    // ── 部署集成：选项选择真正决定部署哪些文件 ──

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task DeployProfileAsync_V1GroupWithoutParentInclude_DeploysOnlyEnabledSelection(int selection, bool enabled)
    {
        var mod = await CreateModAsync("grouped_mod", new ManifestParser().ParseFromJson("""
            {"Version":1,"Name":"grouped_mod","Options":[
              {"Name":"人物","Include":["人物"]},
              {"Name":"材质球","SubOptions":[
                {"Name":"新","Include":["材质球/新"]},
                {"Name":"旧","Include":["材质球/旧"]}]}]}
            """));
        foreach (var (directory, content) in new[] { ("人物", "body"), ("材质球/新", "new"), ("材质球/旧", "old") })
        {
            Directory.CreateDirectory(Path.Combine(mod.Path, directory));
            await File.WriteAllTextAsync(Path.Combine(mod.Path, directory, "9ba626afa44a3aa3.patch_71"), content);
        }
        await File.WriteAllTextAsync(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0"), "unselected root");
        var profileId = await _profileService.CreateProfileAsync("GroupedV1");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        await _profileService.SetModOptionsAsync(profileId, mod.Id, new()
        {
            OptionToggles = [true, enabled], OptionSelections = [0, selection]
        });

        var result = await CreateDeploymentService().DeployProfileAsync(
            profileId, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _gameDirRoot);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(1, result.CompletedMods);
        Assert.Equal(0, result.SkippedMods);
        var data = Path.Combine(_gameDirRoot, "data");
        Assert.Equal("body", await File.ReadAllTextAsync(Path.Combine(data, "9ba626afa44a3aa3.patch_0")));
        if (enabled)
            Assert.Equal(selection == 0 ? "new" : "old", await File.ReadAllTextAsync(Path.Combine(data, "9ba626afa44a3aa3.patch_1")));
        else
            Assert.False(File.Exists(Path.Combine(data, "9ba626afa44a3aa3.patch_1")));
        Assert.False(File.Exists(Path.Combine(data, "9ba626afa44a3aa3.patch_2")));
        Assert.False(File.Exists(Path.Combine(data, "aaaaaaaaaaaaaaaa.patch_0")));
    }

    [Fact]
    public async Task DeployProfileAsync_LegacyOptions_ShouldDeployOnlySelectedSubDirectory()
    {
        // Arrange：v0 模组，两个互斥选项目录 + 根目录杂项文件
        var modDir = Path.Combine(_modDirRoot, "legacy_mod");
        Directory.CreateDirectory(Path.Combine(modDir, "OptionA"));
        Directory.CreateDirectory(Path.Combine(modDir, "OptionB"));
        await File.WriteAllTextAsync(Path.Combine(modDir, "readme.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(modDir, "OptionA", "aaaaaaaaaaaaaaaa.patch_0"), "a");
        await File.WriteAllTextAsync(Path.Combine(modDir, "OptionB", "bbbbbbbbbbbbbbbb.patch_0"), "b");

        var mod = await CreateModAsync("legacy_mod", new ModManifest
        {
            Name = "legacy_mod",
            ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "OptionA", "OptionB" }
        });
        mod.Path = modDir;
        await _modRepository.UpdateAsync(mod);

        var profileId = await _profileService.CreateProfileAsync("DeployP");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        await _profileService.SetModOptionsAsync(profileId, mod.Id,
            new ProfileModOptionsState { LegacySelectedIndex = 1 });

        // Act
        var result = await CreateDeploymentService().DeployProfileAsync(
            profileId, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _gameDirRoot);

        // Assert：只部署选中的 OptionB 子目录，且文件拍平到游戏 data 目录（旧版语义）；
        // 根目录杂项与 OptionA 的文件不部署
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(1, result.CompletedMods);
        Assert.True(File.Exists(Path.Combine(_gameDirRoot, "data", "bbbbbbbbbbbbbbbb.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "OptionB", "bbbbbbbbbbbbbbbb.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "aaaaaaaaaaaaaaaa.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "readme.txt")));
    }

    [Fact]
    public async Task DeployProfileAsync_V1Options_ShouldDeployEnabledOptionIncludes()
    {
        // Arrange：v1 模组，两个选项各自 Include 一个目录
        var modDir = Path.Combine(_modDirRoot, "v1_mod");
        Directory.CreateDirectory(Path.Combine(modDir, "ArmorA"));
        Directory.CreateDirectory(Path.Combine(modDir, "ArmorB"));
        await File.WriteAllTextAsync(Path.Combine(modDir, "ArmorA", "1111111111111111.patch_0"), "a");
        await File.WriteAllTextAsync(Path.Combine(modDir, "ArmorB", "2222222222222222.patch_0"), "b");

        var mod = await CreateModAsync("v1_mod", new ModManifest
        {
            Name = "v1_mod",
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "ArmorA", Include = new List<string> { "ArmorA" } },
                new() { Name = "ArmorB", Include = new List<string> { "ArmorB" } }
            }
        });
        mod.Path = modDir;
        await _modRepository.UpdateAsync(mod);

        var profileId = await _profileService.CreateProfileAsync("DeployV1");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        // 只启用 ArmorB
        await _profileService.SetModOptionsAsync(profileId, mod.Id,
            new ProfileModOptionsState { OptionToggles = new List<bool> { false, true } });

        // Act
        var result = await CreateDeploymentService().DeployProfileAsync(
            profileId, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _gameDirRoot);

        // Assert：启用的选项文件拍平部署到游戏 data 目录
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(_gameDirRoot, "data", "2222222222222222.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "1111111111111111.patch_0")));
    }

    [Fact]
    public async Task DeployProfileAsync_NoManifest_NoSubDirs_ShouldDeployFullDirectory()
    {
        // Arrange：无清单且没有子目录的模组（目录推断不适用）部署根目录合法补丁
        var modDir = Path.Combine(_modDirRoot, "plain_mod");
        Directory.CreateDirectory(modDir);
        await File.WriteAllTextAsync(Path.Combine(modDir, "3333333333333333.patch_0"), "r");
        await File.WriteAllTextAsync(Path.Combine(modDir, "4444444444444444.patch_0"), "l");

        var mod = await CreateModAsync("plain_mod", null);
        mod.Path = modDir;
        await _modRepository.UpdateAsync(mod);

        var profileId = await _profileService.CreateProfileAsync("DeployPlain");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // Act
        var result = await CreateDeploymentService().DeployProfileAsync(
            profileId, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _gameDirRoot);

        // Assert
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(_gameDirRoot, "data", "3333333333333333.patch_0")));
        Assert.True(File.Exists(Path.Combine(_gameDirRoot, "data", "4444444444444444.patch_0")));
    }

    [Fact]
    public async Task DeployProfileAsync_NoManifest_WithSubDirs_ShouldTreatSubDirsAsLegacyOptions()
    {
        // Arrange：无清单但有子目录的模组——按旧版管理器语义把子目录推断为互斥选项，
        // 默认选第 0 个，只部署选中子目录的文件（拍平），根目录散文件不部署
        var modDir = Path.Combine(_modDirRoot, "inferred_mod");
        Directory.CreateDirectory(Path.Combine(modDir, "VariantA"));
        Directory.CreateDirectory(Path.Combine(modDir, "VariantB"));
        await File.WriteAllTextAsync(Path.Combine(modDir, "readme.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(modDir, "VariantA", "aaaaaaaaaaaaaaaa.patch_0"), "a");
        await File.WriteAllTextAsync(Path.Combine(modDir, "VariantB", "bbbbbbbbbbbbbbbb.patch_0"), "b");

        var mod = await CreateModAsync("inferred_mod", null);
        mod.Path = modDir;
        await _modRepository.UpdateAsync(mod);

        // 模拟应用启动时的历史数据回填：推断子目录为互斥选项并落库
        var modService = new ModService(_modRepository, _modDirRoot);
        await modService.GetModsByIdsAsync(new[] { mod.Id });
        var persistedMod = await _modRepository.GetByIdAsync(mod.Id);
        Assert.NotNull(persistedMod!.Manifest);
        Assert.Equal(new List<string> { "VariantA", "VariantB" }, persistedMod.Manifest!.LegacyOptions);

        var profileId = await _profileService.CreateProfileAsync("DeployInferred");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        // Act（未保存过选项：默认选中第一个推断选项）
        var result = await CreateDeploymentService().DeployProfileAsync(
            profileId, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _gameDirRoot);

        // Assert
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(_gameDirRoot, "data", "aaaaaaaaaaaaaaaa.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "bbbbbbbbbbbbbbbb.patch_0")));
        Assert.False(File.Exists(Path.Combine(_gameDirRoot, "data", "readme.txt")));
    }

    private DeploymentService CreateDeploymentService()
    {
        IDeploymentRepository deploymentRepository = new DeploymentRepository(_dbContext);
        IEnumerable<IDeploymentMethod> methods = new IDeploymentMethod[] { new CopyDeploymentMethod() };
        return new DeploymentService(_profileRepository, _modRepository, deploymentRepository, methods);
    }

    // ── 历史数据回填：旧版本导入没有落库 manifest ──

    [Fact]
    public async Task GetModsByIdsAsync_MissingStoredManifest_ShouldBackfillFromModDirectory()
    {
        var modDir = Path.Combine(_modDirRoot, "backfill_mod");
        Directory.CreateDirectory(modDir);
        await File.WriteAllTextAsync(Path.Combine(modDir, "manifest.json"),
            """{"Version":1,"Name":"回填模组","Options":[{"Name":"本体","Include":["本体"]}]}""");

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "backfill_mod",
            Version = "1",
            Path = modDir,
            ImportedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Manifest = null
        };
        await _modRepository.AddAsync(mod);

        var modService = new ModService(_modRepository, _modDirRoot);
        var loaded = (await modService.GetModsByIdsAsync(new[] { mod.Id })).Single();

        Assert.NotNull(loaded.Manifest);
        Assert.Equal(ManifestVersion.V1, loaded.Manifest!.ManifestVersion);
        Assert.Equal("本体", loaded.Manifest.Options![0].Include![0]);

        // 回填已落库：绕过服务直接读仓储也能拿到
        var persisted = await _modRepository.GetByIdAsync(mod.Id);
        Assert.NotNull(persisted!.Manifest);
    }

    // ── 旧版 manifest 格式兼容解析（导入路径依赖） ──

    [Fact]
    public void ParseFromJson_OldV1Format_NumberVersionWithInclude_ShouldParse()
    {
        // 旧版 v1 清单："Version" 是数字，选项带 Include / SubOptions
        const string json = """
            {
              "Version": 1,
              "Guid": "6f1c0d3e-1c2b-4a5d-9e8f-0a1b2c3d4e5f",
              "Name": "Old V1 Mod",
              "Description": "desc",
              "Options": [
                { "Name": "Opt1", "Include": ["Dir1"] },
                { "Name": "Opt2", "Include": ["Dir2"], "SubOptions": [ { "Name": "Var1", "Include": ["Dir2/Var1"] } ] }
              ]
            }
            """;

        var manifest = new ManifestParser().ParseFromJson(json);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest!.ManifestVersion);
        Assert.Equal("Old V1 Mod", manifest.Name);
        Assert.NotNull(manifest.Options);
        Assert.Equal(2, manifest.Options!.Count);
        Assert.Equal("Dir1", manifest.Options[0].Include![0]);
        Assert.Null(manifest.Options[0].DefaultEnabled);
        Assert.Equal("Dir2/Var1", manifest.Options[1].SubOptions![0].Include![0]);
    }

    [Fact]
    public void ParseFromJson_OldLegacyFormat_StringOptions_ShouldParse()
    {
        // 旧版 v0 清单：没有 Version 字段，Options 是互斥子目录名字符串数组
        const string json = """
            {
              "Name": "Old Legacy Mod",
              "Options": ["Low", "High"]
            }
            """;

        var manifest = new ManifestParser().ParseFromJson(json);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.Legacy, manifest!.ManifestVersion);
        Assert.Equal(new List<string> { "Low", "High" }, manifest.LegacyOptions);
    }

    [Fact]
    public void ParseFromJson_LegacyVersionString_ShouldTreatAsV1()
    {
        // 与旧版管理器规则一致：只要存在 Version 字段（即使值是字符串版本号）就按 V1 解析，
        // 否则作者把模组版本号写进 Version 的 v1 清单会被误判为 v0 而丢失选项
        const string json = """
            {
              "Version": "2.5",
              "Name": "String Version Mod",
              "Options": [ { "Name": "Opt1", "Include": ["Dir1"] } ],
            }
            """;

        var manifest = new ManifestParser().ParseFromJson(json);

        Assert.NotNull(manifest);
        Assert.Equal(ManifestVersion.V1, manifest!.ManifestVersion);
        Assert.Equal("2.5", manifest.Version);
        Assert.NotNull(manifest.Options);
        Assert.Equal("Dir1", manifest.Options![0].Include![0]);
    }
}
