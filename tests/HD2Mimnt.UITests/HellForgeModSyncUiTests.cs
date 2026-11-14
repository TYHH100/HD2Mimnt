using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Tests.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task HellForge_DeployButton_ShouldSyncModsIntoModsFolderAndCleanupRemovesThem()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true,
            confirmation: new RecordingConfirmation(),
            workerHostManagerFactory: services => new LoopbackWorkerHost(services));
        using var fixture = HellForgeCliFixture.Create();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();

            // 切换到 HellForge 源并保存。
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            Layout();
            Assert.True(main.SettingsViewModel.ShowHellForgeEntry);

            // 种子模组只有清单没有补丁；给选定模组补一个可部署补丁，并建只含它的配置。
            var modService = scope.Services.GetRequiredService<ModService>();
            var mod = (await modService.GetAllModsAsync()).First();
            await File.WriteAllTextAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), "ui-sync-content");
            var profiles = scope.Services.GetRequiredService<ProfileService>();
            var profileId = await profiles.CreateProfileAsync("HF Sync", "同步测试");
            await profiles.AddModToProfileAsync(profileId, mod.Id);
            await profiles.SetActiveProfileAsync(profileId);

            // 配置页“部署”在 HF 模式改走同步：模组按部署方式直接进 mods\（不经 mod-add），game\data 零写入。
            await main.LibraryViewModel.DeployCommand.ExecuteAsync(profileId);
            Layout();
            Assert.DoesNotContain("mod-add", fixture.ReadArgs());
            var hellForge = scope.Services.GetRequiredService<HellForgeService>();
            var synced = (await hellForge.GetModsAsync()).Mods;
            var info = Assert.Single(synced);
            Assert.StartsWith("hd2mimnt.", info.Id);
            Assert.True(info.Enabled);
            var deployed = Path.Combine(fixture.Root, "mods", $"hd2mimnt.{mod.Id:N}", "data", "9ba626afa44a3aa3.patch_0");
            Assert.True(File.Exists(deployed));
            Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "game", "data")));
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                n => n.Message.Contains("已同步 1 个模组到 HellForge"));

            // 再次同步：内容未变化，跳过重新导出（落盘文件不被重写）。
            var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(deployed, marker);
            await main.LibraryViewModel.DeployCommand.ExecuteAsync(profileId);
            Assert.Equal(marker, File.GetLastWriteTimeUtc(deployed));

            // HF 专页同步状态卡：显示已同步模组数与来源配置名（只读，入口仍在配置页）。
            await main.NavigateToHellForgeCommand.ExecuteAsync(null);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.True(hf.HasSync);
            Assert.Equal(1, hf.SyncedModCount);
            var hfView = window.GetVisualDescendants().OfType<HellForgeView>().Single();
            Assert.Contains("HF Sync", hfView.FindControl<TextBlock>("SyncSourceText")!.Text);
            Assert.False(string.IsNullOrEmpty(hfView.FindControl<TextBlock>("SyncStatusText")!.Text));

            // “清理部署”在 HF 模式移除本管理器同步的模组。
            main.NotificationPanelViewModel.DismissAllToasts();
            await main.LibraryViewModel.CleanupCommand.ExecuteAsync(null);
            Layout();
            // 清理直接删目录 + 清 hellforge-mods.json，不再跑 mod-remove（每次都要重扫整个 mods\）。
            Assert.DoesNotContain("mod-remove", fixture.ReadArgs());
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "mods", $"hd2mimnt.{mod.Id:N}")));
            Assert.Empty((await hellForge.GetModsAsync()).Mods);

            // 清理后专页状态卡回到“尚未同步”。
            await main.NavigateToHellForgeCommand.ExecuteAsync(null);
            await main.HellForgeViewModel!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(main.HellForgeViewModel.HasSync);
        }
        finally { window.DataContext = null; window.Close(); }
    }
}