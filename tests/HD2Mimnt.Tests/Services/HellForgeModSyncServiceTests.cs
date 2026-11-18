using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
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
    private readonly IDeploymentMethod[] _methods =
    [
        new CopyDeploymentMethod(), new SymbolicLinkDeploymentMethod(), new HardLinkDeploymentMethod()
    ];

    private string ModsDir => Path.Combine(_fixture.Root, "mods");

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
        _sync = new HellForgeModSyncService(_hellForge, _profiles, _mods, _settings, _methods,
            Path.Combine(_root, "appdata", "hellforge-sync.json"));
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
        // 扁平化内容确实落位 mods\<id>\data，且导入不经过 mod-add（由本管理器按部署方式直接落盘）。
        Assert.True(File.Exists(Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0")));
        Assert.DoesNotContain(ArgsLines(), l => l.StartsWith("mod-add "));
        // 同步绝不写 game\data。
        Assert.Empty(Directory.GetFiles(Path.Combine(_fixture.Root, "game", "data")));
    }

    [Fact]
    public async Task SyncProfile_CopyMethod_WritesRealFiles()
    {
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(nameof(DeploymentMethod.Copy), result.Method);
        Assert.Empty(result.Warnings);
        var source = Path.Combine(a.Path, $"{BundleX}.patch_0");
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.True(File.Exists(deployed));
        Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(deployed));
        // 复制方式：独立文件而非链接。
        Assert.False(File.GetAttributes(deployed).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(FileIdentity.IsSameFile(source, deployed));
    }

    [Fact]
    public async Task SyncProfile_HardLinkMethod_LinksLibraryFiles()
    {
        await _settings.SetSettingAsync(SettingKeys.DefaultDeploymentMethod, nameof(DeploymentMethod.HardLink));
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(nameof(DeploymentMethod.HardLink), result.Method);
        Assert.Empty(result.Warnings);
        var source = Path.Combine(a.Path, $"{BundleX}.patch_0");
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.True(File.Exists(deployed));
        // 硬链接：与库内文件同一份内容（同卷时链接数 ≥2，文件身份一致）。
        Assert.True(FileIdentity.IsSameFile(source, deployed));
    }

    [Fact]
    public async Task SyncProfile_LinkUnavailable_FallsBackToCopyWithWarning()
    {
        await _settings.SetSettingAsync(SettingKeys.DefaultDeploymentMethod, nameof(DeploymentMethod.HardLink));
        var failing = new FailingLinkMethod();
        var sync = new HellForgeModSyncService(_hellForge, _profiles, _mods, _settings,
            new IDeploymentMethod[] { new CopyDeploymentMethod(), failing },
            Path.Combine(_root, "appdata", "hellforge-sync.json"));
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));

        var result = await sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(nameof(DeploymentMethod.Copy), result.Method);
        Assert.Contains(result.Warnings, warning => warning.Contains("文件复制"));
        Assert.Contains("注意", result.Message);
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(a.Path, $"{BundleX}.patch_0")),
            await File.ReadAllTextAsync(deployed));
    }

    /// <summary>声明为硬链接但建链恒失败：用于验证回退路径。</summary>
    private sealed class FailingLinkMethod : IDeploymentMethod
    {
        public string Name => nameof(DeploymentMethod.HardLink);

        public Task<(bool CanDeploy, string? ErrorMessage)> CheckCompatibilityAsync(
            string sourceDirectory, string targetDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult((true, (string?)null));

        public Task<bool> DeployFileAsync(string sourcePath, string targetPath,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> IsUpToDateAsync(string sourcePath, string targetPath,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> CleanupFileAsync(string targetPath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
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
    public async Task SyncProfile_SkipsLoaders_WithReason()
    {
        var normal = await CreateFlatMod("Normal");
        var loader = await CreateFlatMod("Loader", mod => { mod.BslRole = BslModRole.Loader; mod.BslDetectionVersion = int.MaxValue; });
        var profile = await CreateProfile((normal, true, null), (loader, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 设计性排除不把整次同步判成失败，但原因必须写进文案，否则用户只能看到“跳过”看不到“为什么”。
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(1, result.SyncedCount);
        var skip = Assert.Single(result.SkippedMods);
        Assert.Equal("Loader", skip.Name);
        Assert.Contains("加载器", skip.Reason);
        Assert.True(skip.Expected);
        Assert.Contains("Loader（Lua 加载器由 HellForge 模组页管理，不参与同步）", result.Message);
    }

    [Fact]
    public async Task SyncProfile_DependencyLuaMod_IsSyncedWithItsLuaPatch()
    {
        // Dependency 是“声明依赖加载器的内容模组”（BSL/HD2Runtime 的 Lua 模组）：HellForge 本身支持这类模组，
        // 其 Lua 就在补丁包内（Mods.cs HoldsLua），补丁按原字节复制即可，不应与加载器本体一起被跳过。
        var luaMod = await CreateFlatMod("LuaContent", mod =>
        {
            mod.BslRole = BslModRole.Dependency;
            mod.BslDetectionVersion = int.MaxValue;
            mod.ContainsScripts = true;
        });
        var profile = await CreateProfile((luaMod, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Empty(result.SkippedMods);
        Assert.Equal(1, result.SyncedCount);
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{luaMod.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.True(File.Exists(deployed));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(luaMod.Path, $"{BundleX}.patch_0")),
            await File.ReadAllTextAsync(deployed));
    }

    [Fact]
    public async Task SyncProfile_IasMod_SyncsDataPatchAndWritesNativeFiles()
    {
        // HD2IAS 是混合体：data 补丁由 HellForge 暂存，bin\HD2IndependentRig 的原生文件 HellForge 没有承载机制，
        // 由本管理器直写它的游戏目录并登记归属。
        var ias = await CreateIasMod("IasModel");
        var profile = await CreateProfile((ias, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Empty(result.SkippedMods);
        Assert.Equal(1, result.SyncedCount);
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{ias.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.True(File.Exists(deployed),
            $"synced={result.SyncedCount}; dirs={string.Join(" | ", Directory.GetDirectories(ModsDir).Select(Path.GetFileName))}");
        var native = Path.Combine(_fixture.Root, "game", "bin", "HD2IndependentRig", "hero.rigbin");
        Assert.True(File.Exists(native));
        Assert.Equal("rig-bytes", await File.ReadAllTextAsync(native));
        // 作者元数据不落游戏目录。
        Assert.False(File.Exists(Path.Combine(_fixture.Root, "game", "bin", "HD2IndependentRig", "hero.hd2author")));
        // 归属写进状态文件，供禁用/清理时只删自己写的文件。
        var state = await File.ReadAllTextAsync(Path.Combine(_root, "appdata", "hellforge-sync.json"));
        Assert.Contains("bin/HD2IndependentRig/hero.rigbin", state);
    }

    [Fact]
    public async Task SyncProfile_IasModDisabled_RemovesOwnedNativeFiles()
    {
        var ias = await CreateIasMod("IasModel");
        var profile = await CreateProfile((ias, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);
        var native = Path.Combine(_fixture.Root, "game", "bin", "HD2IndependentRig", "hero.rigbin");
        Assert.True(File.Exists(native));

        // 禁用后重新同步：上一次写的原生文件必须一起撤掉，不能留在游戏目录里。
        profile.Mods[0].Enabled = false;
        await _profiles.UpdateAsync(profile);
        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.False(File.Exists(native));
        Assert.False(Directory.Exists(Path.Combine(ModsDir, $"hd2mimnt.{ias.Id:N}")));
    }

    /// <summary>按导入后的形态造一个 HD2IAS 模组：components\0 的 data 补丁 + components\1 的原生 rig 文件。</summary>
    private async Task<Mod> CreateIasMod(string name)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_library, id.ToString("N"));
        var dataDirectory = Path.Combine(directory, "components", "0");
        var rigDirectory = Path.Combine(directory, "components", "1", "HD2IndependentRig");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(rigDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, $"{BundleX}.patch_0"), name + "-model");
        await File.WriteAllTextAsync(Path.Combine(rigDirectory, "hero.rigbin"), "rig-bytes");
        await File.WriteAllTextAsync(Path.Combine(rigDirectory, "hero.hd2author"), "{\"Author\":\"test\"}");
        var logical = new LogicalMod
        {
            Id = id, Name = name, Version = "1.0.0", Author = "test",
            Components =
            [
                new LogicalModComponent
                {
                    ComponentId = "model-0", Type = ComponentType.Model, Name = "Model", SourcePackageId = id,
                    DeployableFiles =
                    [
                        new DeployableFile
                        {
                            SourcePath = $"components/0/{BundleX}.patch_0", TargetPath = $"data/{BundleX}.patch_0"
                        }
                    ]
                },
                new LogicalModComponent
                {
                    ComponentId = "rig-0", Type = ComponentType.HD2IAS, Name = "Rig", SourcePackageId = id,
                    DeployableFiles =
                    [
                        new DeployableFile
                        {
                            SourcePath = "components/1/HD2IndependentRig/hero.rigbin",
                            TargetPath = "bin/HD2IndependentRig/hero.rigbin"
                        },
                        new DeployableFile
                        {
                            SourcePath = "components/1/HD2IndependentRig/hero.hd2author",
                            TargetPath = "bin/HD2IndependentRig/hero.hd2author"
                        }
                    ]
                }
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(directory, ".hd2mimnt-hd2ir.json"),
            System.Text.Json.JsonSerializer.Serialize(logical));
        var manifest = new ModManifest
        {
            Guid = id, Name = name, ManifestVersion = ManifestVersion.V1,
            Options =
            [
                new ModOption { Name = "Model", Include = ["components/0"] },
                new ModOption { Name = "Rig", Include = ["components/1/HD2IndependentRig"] }
            ]
        };
        var mod = new Mod
        {
            Id = id, Name = name, Version = "1.0.0", Path = directory, Manifest = manifest,
            WorkflowType = Hd2IrIntegrationService.Workflow, LogicalMod = logical,
            Category = ModCategory.Armor, ImportedAt = DateTime.UtcNow, ThumbnailPath = string.Empty
        };
        await _mods.AddAsync(mod);
        return mod;
    }

    [Fact]
    public async Task SyncProfile_SameSkipReason_GroupsNamesInMessage()
    {
        var a = await CreateFlatMod("Loader_A", mod => { mod.BslRole = BslModRole.Loader; mod.BslDetectionVersion = int.MaxValue; });
        var b = await CreateFlatMod("Loader_B", mod => { mod.BslRole = BslModRole.CslLoader; mod.BslDetectionVersion = int.MaxValue; });
        var profile = await CreateProfile((a, true, null), (b, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        // 名称顺序随部署序列（加载器优先级）变化，只校验同原因合并成一组、原因只出现一次。
        Assert.Contains("跳过 2 个：", result.Message);
        Assert.Contains("Loader_A", result.Message);
        Assert.Contains("Loader_B", result.Message);
        const string reason = "（Lua 加载器由 HellForge 模组页管理，不参与同步）";
        Assert.Equal(1, result.Message.Split(reason).Length - 1);
    }

    [Fact]
    public async Task SyncProfile_MissingModFolder_StillReportsPartialFailure()
    {
        var missing = await CreateFlatMod("Gone");
        Directory.Delete(missing.Path, recursive: true);
        var profile = await CreateProfile((missing, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 文件缺失不是设计性排除：仍要提示未完全成功。
        Assert.Equal(DeploymentStatus.PartiallyFailed, result.Status);
        var skip = Assert.Single(result.SkippedMods);
        Assert.Equal("模组文件缺失", skip.Reason);
        Assert.False(skip.Expected);
        Assert.Contains("Gone（模组文件缺失）", result.Message);
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
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0");
        // 打上旧时间戳：若第二次同步重新导出（复制），该文件会被重写而丢失这个时间戳。
        var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(deployed, marker);
        var second = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, first.UpdatedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.UnchangedCount);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(deployed));
    }

    [Fact]
    public async Task SyncProfile_ContentChanged_ReplacesFolderWithoutDuplicates()
    {
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        var source = Path.Combine(a.Path, $"{BundleX}.patch_0");
        await File.WriteAllTextAsync(source, "changed-content");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, result.UpdatedCount);
        var deployed = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}", "data", $"{BundleX}.patch_0");
        Assert.Equal("changed-content", await File.ReadAllTextAsync(deployed));
        // 更新同 id 模组：mods\ 下仍只有一个模组目录，不留旧副本。
        Assert.Single(Directory.GetDirectories(ModsDir));
        Assert.Single((await _hellForge.GetModsAsync()).Mods);
    }

    [Fact]
    public async Task SyncProfile_TwoPacksWithSameId_SecondBecomesFlat()
    {
        var guid = Guid.NewGuid();
        var first = await CreatePackMod("Pack A", guid,
            new ModOption { Name = "A", Include = new List<string> { "A" } });
        var second = await CreatePackMod("Pack B", guid,
            new ModOption { Name = "B", Include = new List<string> { "B" } });
        var profile = await CreateProfile((first, true, null), (second, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 同 Guid 的两个外观包会被 HellForge 判成重复 id、两个都不加载：后一个改成扁平导出（唯一 id），内容不丢。
        Assert.True(result.Status == DeploymentStatus.Completed, result.ErrorMessage);
        Assert.Contains(result.Warnings, w => w.Contains("pack."));
        var folders = Directory.GetDirectories(ModsDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { $"hd2mimnt.{second.Id:N}", $"pack.{guid:D}" }, folders);
        Assert.Equal(2, (await _hellForge.GetModsAsync()).Mods.Count);
    }

    [Fact]
    public async Task SyncProfile_CompositePackWithoutRootManifest_IsRecognized()
    {
        // 复合包（多个子包合并）根目录没有 manifest.json：只搬 Include 目录会被 HellForge 静默忽略。
        var pack = await CreatePackMod("Composite",
            new ModOption { Name = "本体", Include = new List<string> { "本体/本体" } },
            new ModOption { Name = "材质球", Include = new List<string> { "材质球/深色" } });
        File.Delete(Path.Combine(pack.Path, "manifest.json"));
        var profile = await CreateProfile((pack, true, null));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var folder = Path.Combine(ModsDir, $"pack.{pack.Manifest!.Guid:D}");
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
        var info = Assert.Single((await _hellForge.GetModsAsync()).Mods);
        Assert.Equal($"pack.{pack.Manifest!.Guid:D}", info.Id);
        Assert.Equal(2, info.Options!.Count);
    }

    [Fact]
    public async Task SyncProfile_DeletesBrokenLeftoverFolders()
    {
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 历史版本每次同步都新开一个 id-N 目录（HellForge 认不出内容 → 不列出 → 下次又新开）；重新同步应清掉。
        var leftover = Path.Combine(ModsDir, $"hd2mimnt.{a.Id:N}-2");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "junk.txt"), "junk");
        var source = Path.Combine(a.Path, $"{BundleX}.patch_0");
        await File.WriteAllTextAsync(source, "changed-again");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(2));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(1, result.UpdatedCount);
        Assert.False(Directory.Exists(leftover));
        Assert.Single(Directory.GetDirectories(ModsDir));
    }

    [Fact]
    public async Task SyncProfile_UnchangedSync_RunsNoCliExceptSessionStatus()
    {
        // 未变化时只保留 status（会话拦截），不再跑 mods / mod-order / mod-pack / mod-switch：
        // 这些命令每次都要重扫整个 mods\，几十个外观包时单次 5 秒上下，是“同步很慢”的主要来源。
        var pack = await CreatePackMod("Pack",
            new ModOption { Name = "A", Include = new List<string> { "A" } },
            new ModOption { Name = "B", Include = new List<string> { "B" } });
        var optionsJson = new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } }.ToJson();
        var profile = await CreateProfile((pack, true, optionsJson));
        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        var statePath = Path.Combine(ModsDir, "hellforge-mods.json");
        var stateBefore = await File.ReadAllTextAsync(statePath);
        File.Delete(_fixture.ArgsFile);

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(1, result.UnchangedCount);
        var commands = ArgsLines().Select(line => line.Split(' ')[0]).Distinct().ToList();
        Assert.Equal(new[] { "status" }, commands);
        // 状态文件也不该被无谓重写（HellForge 的 Mods 页靠文件时间判断外部改动）。
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task SyncProfile_StateFileWrite_PreservesUnrelatedFieldsAndEntries()
    {
        // 官方允许外部写 hellforge-mods.json：只改自己的 id，别人的条目与其他字段必须原样保留。
        var pack = await CreatePackMod("Pack", new ModOption { Name = "A", Include = new List<string> { "A" } });
        var optionsJson = new ProfileModOptionsState { OptionToggles = new List<bool> { false } }.ToJson();
        var profile = await CreateProfile((pack, true, optionsJson));
        Directory.CreateDirectory(ModsDir);
        // order 预置成同步后的样子，避免触发 mod-order（替身会按自己的模型重写文件，丢掉未知字段）。
        var packId = $"pack.{pack.Id:D}";
        await File.WriteAllTextAsync(Path.Combine(ModsDir, "hellforge-mods.json"), $$"""
            {
              "format": 1,
              "disabled": ["someone.else"],
              "order": ["{{packId}}", "someone.else"],
              "packs": { "pack.00000000-0000-0000-0000-000000000000": { "off": ["X"] } },
              "share": false,
              "showEditedStats": true
            }
            """);

        await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(ModsDir, "hellforge-mods.json")));
        var root = document.RootElement;
        // 别人的模组保持关闭、仍排在前面；pack 条目保留。
        Assert.Contains("someone.else", root.GetProperty("disabled").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("pack.00000000-0000-0000-0000-000000000000", root.GetProperty("packs").EnumerateObject().Select(p => p.Name));
        // 我们的模组被打开（不在 disabled 里）。
        var info = Assert.Single((await _hellForge.GetModsAsync()).Mods);
        Assert.True(info.Enabled);
        // 未知字段原样保留。
        Assert.False(root.GetProperty("share").GetBoolean());
        Assert.True(root.GetProperty("showEditedStats").GetBoolean());
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
        // 目录由本管理器写，直接删目录 + 清 hellforge-mods.json 条目，不经 mod-remove（那要重扫整个 mods\）。
        Assert.False(Directory.Exists(Path.Combine(ModsDir, $"hd2mimnt.{b.Id:N}")));
        Assert.DoesNotContain(ArgsLines(), l => l.StartsWith("mod-remove "));
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
    public async Task SyncProfile_CompositePackWithDuplicateOptionNames_SyncsAndTogglesEachOne()
    {
        // 复合包合并后选项平铺到同一层，重名很常见（两个子包都有“本体”）。
        // 旧实现按名字建字典，直接抛“An item with the same key has already been added. Key: 本体”。
        var pack = await CreatePackMod("Composite",
            new ModOption { Name = "本体", Include = new List<string> { "A/本体" } },
            new ModOption { Name = "上衣", Include = new List<string> { "A/上衣" } },
            new ModOption { Name = "本体", Include = new List<string> { "B/本体" } });
        var optionsJson = new ProfileModOptionsState
        {
            OptionToggles = new List<bool> { true, false, false }
        }.ToJson();
        var profile = await CreateProfile((pack, true, optionsJson));

        var result = await _sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        Assert.True(result.Status == DeploymentStatus.Completed, result.ErrorMessage);
        var info = Assert.Single((await _hellForge.GetModsAsync()).Mods);
        // 导出清单给第二个“本体”改名，两个选项才能各自开关。
        Assert.Equal(new[] { "本体", "上衣", "本体 (2)" }, info.Options!.Select(option => option.Name));
        Assert.True(info.Options![0].On);
        Assert.False(info.Options[1].On);
        Assert.False(info.Options[2].On);
        // 状态文件里两个“本体”各自独立：只有第二个被关（第一项仍开）。
        using var state = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ModsDir, "hellforge-mods.json")));
        var off = state.RootElement.GetProperty("packs").GetProperty(info.Id).GetProperty("off")
            .EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(new[] { "上衣", "本体 (2)" }, off);
        Assert.DoesNotContain(ArgsLines(), l => l.StartsWith("mod-pack "));
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
        // 选项写进 hellforge-mods.json（官方允许外部写），HellForge 的列表读到的就是 V2。
        Assert.Equal("V2", info.Options!.Single(o => o.Name == "A").Picked);
        Assert.DoesNotContain(ArgsLines(), l => l.StartsWith("mod-pack "));
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

    [Fact]
    public async Task SyncProfile_RemovesLegacyExportCache()
    {
        var legacy = Path.Combine(_root, "legacy-export");
        Directory.CreateDirectory(Path.Combine(legacy, "junk"));
        var sync = new HellForgeModSyncService(_hellForge, _profiles, _mods, _settings, _methods,
            Path.Combine(_root, "appdata", "hellforge-sync.json"), null, legacy);
        var a = await CreateFlatMod("A");
        var profile = await CreateProfile((a, true, null));

        await sync.SyncProfileAsync(profile.Id, DeploymentOrder.TopToBottom);

        // 旧版本导出缓存不再使用，同步后清掉。
        Assert.False(Directory.Exists(legacy));
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

    private Task<Mod> CreatePackMod(string name, params ModOption[] options)
        => CreatePackMod(name, null, options);

    private async Task<Mod> CreatePackMod(string name, Guid? manifestGuid, params ModOption[] options)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_library, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = new ModManifest
        {
            Guid = manifestGuid ?? id, Name = name, ManifestVersion = ManifestVersion.V1, Options = options.ToList()
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