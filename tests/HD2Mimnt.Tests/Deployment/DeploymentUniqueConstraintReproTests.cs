using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Deployment;

/// <summary>
/// 复现真机报错 "SQLite Error 19: UNIQUE constraint failed: DeploymentFiles.Id"（改模组选项后重新部署，概率性）。
/// 场景一：单进程选项变更重部署；场景二：双服务实例并发部署（模拟 UI 自动对账 + Worker 部署跨进程）；
/// 场景三：同一 (GamePath, ModId) 重复记录（迁移失败残留）下的部署。
/// </summary>
[Trait("Category", "Slow")]
public class DeploymentUniqueConstraintReproTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly string _testSourceDir;
    private readonly string _testTargetDir;

    public DeploymentUniqueConstraintReproTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"repro_unique_{Guid.NewGuid()}.db");
        _testSourceDir = Path.Combine(Path.GetTempPath(), $"repro_src_{Guid.NewGuid()}");
        _testTargetDir = Path.Combine(Path.GetTempPath(), $"repro_tgt_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testSourceDir);
        Directory.CreateDirectory(_testTargetDir);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        if (Directory.Exists(_testSourceDir)) Directory.Delete(_testSourceDir, true);
        if (Directory.Exists(_testTargetDir)) Directory.Delete(_testTargetDir, true);
    }

    private static HD2MimntDbContext CreateContext(string dbPath)
    {
        var context = new HD2MimntDbContext(dbPath);
        context.InitializeDatabaseAsync().Wait();
        return context;
    }

    /// <summary>构造带两个选项的模组：同前缀补丁分布在不同选项目录，切换选项会触发补丁编号重排（Move）。</summary>
    private static async Task<Mod> AddOptionModAsync(ModRepository modRepository, string sourceRoot, string name)
    {
        var path = Path.Combine(sourceRoot, name);
        Directory.CreateDirectory(Path.Combine(path, "v1"));
        Directory.CreateDirectory(Path.Combine(path, "v2"));
        await File.WriteAllTextAsync(Path.Combine(path, "v1", "aaaaaaaaaaaaaaaa.patch_5"), "v1-content");
        await File.WriteAllTextAsync(Path.Combine(path, "v2", "aaaaaaaaaaaaaaaa.patch_6"), "v2-content");
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            Manifest = new ModManifest
            {
                ManifestVersion = ManifestVersion.V1,
                Options = [new ModOption { Name = "one", Include = ["v1"] },
                    new ModOption { Name = "two", Include = ["v2"] }]
            }
        };
        await modRepository.AddAsync(mod);
        return mod;
    }

    private async Task<Profile> AddProfileAsync(ProfileRepository profileRepository, ModRepository modRepository, Mod mod)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = "repro profile" };
        await profileRepository.AddAsync(profile);
        await new ProfileService(profileRepository, modRepository).AddModToProfileAsync(profile.Id, mod.Id);
        return profile;
    }

    private static async Task SetOptionTogglesAsync(ProfileRepository profileRepository, Profile profile, bool one, bool two)
    {
        var current = await profileRepository.GetByIdAsync(profile.Id);
        current!.Mods[0].OptionsJson = System.Text.Json.JsonSerializer.Serialize(
            new ProfileModOptionsState { OptionToggles = [one, two] });
        await profileRepository.UpdateAsync(current);
    }

    [Fact]
    public async Task OptionChangeRedeploy_SingleProcess_ShouldCompleteWithoutUniqueError()
    {
        using var context = CreateContext(_testDbPath);
        var modRepository = new ModRepository(context);
        var profileRepository = new ProfileRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var deploymentService = new DeploymentService(profileRepository, modRepository, deploymentRepository,
            [new CopyDeploymentMethod()],
            stateMigration: new DeploymentStateMigrationService(context));

        var mod = await AddOptionModAsync(modRepository, _testSourceDir, "option-mod");
        var profile = await AddProfileAsync(profileRepository, modRepository, mod);

        var first = await deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, first.Status);

        // 反复"改选项 → 重新部署"，覆盖编号重排（Move）与增删组合；全关组合会按设计拒绝部署，不在循环内。
        foreach (var (one, two) in new[] { (false, true), (true, true), (true, false), (false, true), (true, true) })
        {
            await SetOptionTogglesAsync(profileRepository, profile, one, two);
            var result = await deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, _testTargetDir);
            Assert.True(string.IsNullOrEmpty(result.ErrorMessage),
                $"改选项({one},{two})后重部署失败：{result.ErrorMessage}");
            Assert.Equal(DeploymentStatus.Completed, result.Status);
        }
    }

    [Fact]
    public async Task ConcurrentDeploys_TwoServiceInstances_ShouldNotHitUniqueConstraint()
    {
        using var context = CreateContext(_testDbPath);
        var modRepository = new ModRepository(context);
        var profileRepository = new ProfileRepository(context);
        var deploymentRepository = new DeploymentRepository(context);

        var mod = await AddOptionModAsync(modRepository, _testSourceDir, "concurrent-mod");
        var profile = await AddProfileAsync(profileRepository, modRepository, mod);

        // 预置一次成功部署，让两个并发实例都面对"已有当前状态记录"的库。
        var bootstrap = new DeploymentService(profileRepository, modRepository, deploymentRepository,
            [new CopyDeploymentMethod()]);
        var initial = await bootstrap.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, initial.Status);

        // 两个实例各自带独立迁移服务（_applyGate/迁移闸门均为实例级），
        // 对应正式版 UI 进程与 Worker 子进程各持一个 DeploymentService 的真实形态。
        for (var round = 0; round < 6; round++)
        {
            using var contextA = CreateContext(_testDbPath);
            using var contextB = CreateContext(_testDbPath);
            var serviceA = new DeploymentService(new ProfileRepository(contextA), new ModRepository(contextA),
                new DeploymentRepository(contextA), [new CopyDeploymentMethod()],
                stateMigration: new DeploymentStateMigrationService(contextA));
            var serviceB = new DeploymentService(new ProfileRepository(contextB), new ModRepository(contextB),
                new DeploymentRepository(contextB), [new CopyDeploymentMethod()],
                stateMigration: new DeploymentStateMigrationService(contextB));

            await SetOptionTogglesAsync(profileRepository, profile, round % 2 == 0, round % 2 == 1);
            var taskA = serviceA.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, _testTargetDir);
            var taskB = serviceB.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, _testTargetDir);
            var results = await Task.WhenAll(taskA, taskB);

            var uniqueFailures = results.Where(result => result.ErrorMessage?.Contains("UNIQUE constraint") == true)
                .ToList();
            Assert.True(uniqueFailures.Count == 0,
                $"第 {round} 轮并发部署复现唯一键冲突：{string.Join(" | ", uniqueFailures.Select(result => result.ErrorMessage))}");
        }
    }

    [Fact]
    public async Task DuplicateCurrentStateRecords_ShouldReproduceUniqueConstraintOnFiles()
    {
        using var context = CreateContext(_testDbPath);
        var modRepository = new ModRepository(context);
        var profileRepository = new ProfileRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var deploymentService = new DeploymentService(profileRepository, modRepository, deploymentRepository,
            [new CopyDeploymentMethod()],
            // 迁移被注入为 null：模拟"懒迁移失败被吞掉后带脏状态继续部署"的路径。
            stateMigration: null);

        var mod = await AddOptionModAsync(modRepository, _testSourceDir, "dirty-mod");
        var profile = await AddProfileAsync(profileRepository, modRepository, mod);
        var first = await deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, first.Status);

        // 手工制造同一 (GamePath, ModId) 的两条"当前状态"记录，文件行只挂在 StartTime 更早的一条上：
        // 对账读取按 StartTime 排序 → 重用早记录的文件 Id；
        // 保存解析 find 无 ORDER BY → 按 rowid 返回另一条 → 先删后插删不掉早记录的文件行 → 插入撞键。
        // 对应老库残留或跨进程 find-then-insert 竞态后，懒迁移未能合并的库面。
        var gamePath = DeploymentPlanBuilderProbe.Normalize(_testTargetDir);
        var original = (await deploymentRepository.GetCurrentDeploymentsAsync(gamePath)).Single();
        var ownerOfFiles = new DeploymentRecord  // rowid 大（后插）：持有文件行，StartTime 早（对账读取序在前）
        {
            Id = Guid.NewGuid(),
            SessionId = original.SessionId,
            ProfileId = original.ProfileId,
            ModId = original.ModId,
            Workflow = original.Workflow,
            DeploymentMethod = original.DeploymentMethod,
            StartTime = original.StartTime.AddSeconds(-10),
            EndTime = original.EndTime,
            Status = original.Status,
            OptionsSnapshotJson = original.OptionsSnapshotJson,
            DeploymentOrderIndex = original.DeploymentOrderIndex,
            GamePath = original.GamePath
        };
        var targetOfFind = new DeploymentRecord  // rowid 小（先插）：find LIMIT 1 返回它，但无文件
        {
            Id = Guid.NewGuid(),
            SessionId = original.SessionId,
            ProfileId = original.ProfileId,
            ModId = original.ModId,
            Workflow = original.Workflow,
            DeploymentMethod = original.DeploymentMethod,
            StartTime = original.StartTime,
            EndTime = original.EndTime,
            Status = original.Status,
            OptionsSnapshotJson = original.OptionsSnapshotJson,
            DeploymentOrderIndex = original.DeploymentOrderIndex,
            GamePath = original.GamePath
        };
        // 先插 targetOfFind（rowid 小），再插 ownerOfFiles（rowid 大）。
        await deploymentRepository.AddAsync(targetOfFind);
        await deploymentRepository.AddAsync(ownerOfFiles);
        // 原记录及其文件删除，文件行改为挂到 ownerOfFiles 下（Id 全新，SourceKey 与原文件相同）。
        await deploymentRepository.DeleteAsync(original.Id);
        await deploymentRepository.DeleteDeploymentFilesByDeploymentIdAsync(original.Id);
        await deploymentRepository.AddDeploymentFilesAsync(original.Files.Select(file => new DeploymentFile
        {
            Id = Guid.NewGuid(),
            DeploymentId = ownerOfFiles.Id,
            ModId = file.ModId,
            SourcePath = file.SourcePath,
            TargetPath = file.TargetPath,
            Method = file.Method,
            GamePath = file.GamePath,
            SourceKey = file.SourceKey,
            SourceRelativePath = file.SourceRelativePath
        }));

        // 改选项触发元数据保存 + 补丁编号重排（文件 Id 重用窗口）。
        await SetOptionTogglesAsync(profileRepository, profile, false, true);
        var result = await deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        // 机制验证：重复记录 + 记录解析 LIMIT 1 只删一条的文件，重用 Id 插入撞上另一条残留行。
        Assert.True(result.ErrorMessage?.Contains("UNIQUE constraint failed: DeploymentFiles.Id") == true,
            $"预期复现 UNIQUE constraint failed: DeploymentFiles.Id，实际：{result.ErrorMessage ?? "（成功无错）"}");
    }
}

/// <summary>测试内访问 internal 归一化逻辑的探针。</summary>
internal static class DeploymentPlanBuilderProbe
{
    public static string Normalize(string gamePath) => DeploymentPlanBuilder.NormalizeGamePath(gamePath);
}
