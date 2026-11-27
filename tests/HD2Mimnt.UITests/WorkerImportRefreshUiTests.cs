using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

/// <summary>
/// 回归：导入在 HD2Mimnt.Worker 独立进程内写库，不会触发主进程 ModService.ModsChanged，
/// 配置页必须靠 LibraryViewModel.ImportedModsChanged 显式联动刷新，否则要重启才能看到新模组。
/// 用回环宿主 + 独立服务图模拟子进程（只有管道传输被替换），避免为每个用例冷启动真实 Worker。
/// </summary>
public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task WorkerImport_ShouldRefreshProfileCards()
    {
        await using var scope = await UiScope.CreateAsync(
            workerHostManagerFactory: services => new LoopbackWorkerHost(services, CreateWorkerScopedModService));
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.ProfilesAutoAddImportedMods, true);
        // 关闭导入后自动兼容性检查：它自身会经 Compatibility.ContentChanged 触发刷新，
        // 会掩盖本用例要验证的跨进程事件缺口。
        await settings.SetSettingAsync(SettingKeys.CompatibilityAutoCheckImport, false);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var activeId = await profiles.CreateProfileAsync("Worker Import Profile");
        await profiles.SetActiveProfileAsync(activeId);

        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var profile = main.ProfileViewModel;

        var imported = await ImportWorkerModAsync(scope, main);

        Assert.Contains(profile.ProfileMods, card => card.ModId == imported.Id);
        Assert.Equal(imported.Id, Assert.Single(main.LibraryViewModel.Import.NewImportedIds));
        // 用例结束前停掉视图模型的后台轮询与刷新，避免它们在服务释放后落到 xUnit idle 上下文。
        await main.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task WorkerSameGuidImport_ShouldRefreshOpenVersionHistory()
    {
        await using var scope = await UiScope.CreateAsync(
            workerHostManagerFactory: services => new LoopbackWorkerHost(services, CreateWorkerScopedModService));
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        await settings.SetSettingAsync(SettingKeys.CompatibilityAutoCheckImport, false);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var source = Path.Combine(scope.Root, "sources", "worker-version-history");
        Directory.CreateDirectory(source);
        await ManifestPackage.SaveAsync(Path.Combine(source, "manifest.json"), new JsonObject
        {
            ["Version"] = 1, ["Guid"] = Guid.NewGuid().ToString(), ["Name"] = "Worker History Mod",
            ["ModVersion"] = "1.0.0", ["Author"] = "Tester"
        });
        var patch = Path.Combine(source, "data", "sample.patch_0");
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        await File.WriteAllBytesAsync(patch, new byte[] { 1 });
        try
        {
            await main.LibraryViewModel.ImportPathsAsync([source]);
            await WaitForWorkerImportAsync(main);
            var target = Assert.Single(main.LibraryViewModel.FilteredMods);
            main.LibraryViewModel.ShowModDetailCommand.Execute(target);
            var versions = main.LibraryViewModel.LocalVersions;
            await versions.Completion;
            Assert.Empty(versions.Versions);

            await File.WriteAllBytesAsync(patch, new byte[] { 2 });
            await main.LibraryViewModel.ImportPathsAsync([source]);
            await WaitForWorkerImportAsync(main);

            Assert.Single(versions.Versions);
        }
        finally
        {
            await main.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task WorkerSameGuidImport_WithGroupChoice_ShouldKeepExistingGroupMembership()
    {
        await using var scope = await UiScope.CreateAsync(
            workerHostManagerFactory: services => new LoopbackWorkerHost(services, CreateWorkerScopedModService));
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.ProfilesAutoAddImportedMods, true);
        await settings.SetSettingAsync(SettingKeys.ProfilesChooseGroupWhenAdding, true);
        await settings.SetSettingAsync(SettingKeys.AllowDuplicateImports, true);
        await settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        await settings.SetSettingAsync(SettingKeys.CompatibilityAutoCheckImport, false);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var activeId = await profiles.CreateProfileAsync("Worker Same GUID Groups");
        await profiles.SetActiveProfileAsync(activeId);
        var firstGroup = await profiles.AddSeparatorAsync(activeId, "第一组", 0);
        var secondGroup = await profiles.AddSeparatorAsync(activeId, "第二组", 0);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var source = Path.Combine(scope.Root, "sources", "worker-same-guid");
        Directory.CreateDirectory(source);
        await ManifestPackage.SaveAsync(Path.Combine(source, "manifest.json"), new JsonObject
        {
            ["Version"] = 1, ["Guid"] = Guid.NewGuid().ToString(), ["Name"] = "Worker Same GUID Mod",
            ["ModVersion"] = "1.0.0", ["Author"] = "Tester", ["Description"] = "worker same guid"
        });
        var patch = Path.Combine(source, "data", "sample.patch_0");
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        await File.WriteAllBytesAsync(patch, new byte[] { 1 });
        try
        {
            await main.LibraryViewModel.ImportPathsAsync([source]);
            await WaitForWorkerImportAsync(main);
            Assert.True(main.LibraryViewModel.Import.IsImportGroupSelectionVisible);
            main.LibraryViewModel.Import.SelectedImportGroup = main.LibraryViewModel.Import.ImportGroupChoices
                .Single(choice => choice.Id == firstGroup.Id);
            await main.LibraryViewModel.ApplyImportGroupCommand.ExecuteAsync(null);
            var before = (await profiles.GetProfileByIdAsync(activeId))!;
            var beforeMods = before.Mods.OrderBy(item => item.Order).Select(item => item.ModId).ToArray();
            var beforeSeparators = before.Separators.OrderBy(item => item.Order)
                .Select(item => (item.Id, item.Order, item.MemberCount)).ToArray();
            var original = (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(beforeMods[0]))!;

            await File.WriteAllBytesAsync(patch, new byte[] { 2 });
            await main.LibraryViewModel.ImportPathsAsync([source]);
            await WaitForWorkerImportAsync(main);
            if (main.LibraryViewModel.Import.IsImportGroupSelectionVisible)
            {
                main.LibraryViewModel.Import.SelectedImportGroup = main.LibraryViewModel.Import.ImportGroupChoices
                    .Single(choice => choice.Id == secondGroup.Id);
                await main.LibraryViewModel.ApplyImportGroupCommand.ExecuteAsync(null);
            }

            Assert.False(main.LibraryViewModel.Import.IsImportGroupSelectionVisible);
            Assert.Empty(main.LibraryViewModel.Import.NewImportedIds);
            Assert.Equal(beforeMods[0], Assert.Single(main.LibraryViewModel.Import.ImportedIds));
            var after = (await profiles.GetProfileByIdAsync(activeId))!;
            Assert.Equal(beforeMods, after.Mods.OrderBy(item => item.Order).Select(item => item.ModId));
            Assert.Equal(beforeSeparators, after.Separators.OrderBy(item => item.Order)
                .Select(item => (item.Id, item.Order, item.MemberCount)));
            var saved = Assert.Single(await scope.Services.GetRequiredService<ModService>().GetLocalVersionsAsync(beforeMods[0]));
            Assert.Equal(original.Path, saved.Content.Path);
            Assert.Equal("1.0.0", saved.Content.Version);
            Assert.True(Directory.Exists(saved.Content.Path));
            Assert.Equal("1.1.0", (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(beforeMods[0]))!.Version);
        }
        finally
        {
            await main.DisposeAsync();
        }
    }

    private static async Task<ModItemViewModel> ImportWorkerModAsync(UiScope scope, MainWindowViewModel main)
    {
        var source = Path.Combine(scope.Root, "sources", $"worker-mod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        await File.WriteAllBytesAsync(Path.Combine(source, "data", "sample.patch_0"), new byte[] { 1, 2, 3 });
        await ManifestPackage.SaveAsync(Path.Combine(source, "manifest.json"), new JsonObject
        {
            ["Version"] = 1, ["Guid"] = Guid.NewGuid().ToString(), ["Name"] = "Worker Imported Mod",
            ["ModVersion"] = "1.0.0", ["Author"] = "Tester", ["Description"] = "worker import"
        });

        await main.LibraryViewModel.ImportPathsAsync(new[] { source });
        await WaitForWorkerImportAsync(main);

        return Assert.Single(main.LibraryViewModel.FilteredMods.Where(mod => mod.Name == "Worker Imported Mod"));
    }

    private static async Task WaitForWorkerImportAsync(MainWindowViewModel main)
    {
        for (var attempt = 0; attempt < 600 && !main.LibraryViewModel.ImportCompletion.IsCompleted; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        await main.LibraryViewModel.ImportCompletion;
        await main.LibraryViewModel.ImportPostProcessing;
        await main.WaitForRefreshesAsync();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 模拟 Worker 子进程的服务图：独立 ModService/ProfileService 实例共用同一数据库，
    /// 因此写库生效但主进程实例的 ModsChanged/ProfilesChanged 不会触发。
    /// </summary>
    private static ModService CreateWorkerScopedModService(IServiceProvider services)
    {
        var mods = services.GetRequiredService<IModRepository>();
        var locations = services.GetRequiredService<StorageSettingsService>().Current;
        var settings = services.GetRequiredService<SettingsService>();
        var workerProfiles = new ProfileService(
            services.GetRequiredService<IProfileRepository>(), mods, settings);
        return new ModService(mods, locations.ModsPath, logger: null, deploymentService: null,
            tempDirectory: locations.TempPath, settingsService: settings, profileService: workerProfiles);
    }
}
