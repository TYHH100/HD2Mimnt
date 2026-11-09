using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ProfileDeploymentStatus_ShouldBeIndependentOfEnabledStateAndClearAfterCleanup()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var mod = vm.ProfileMods.First(row => !row.IsSeparator);
        var window = new Window { Content = new ProfileView { DataContext = vm }, Width = 1200, Height = 950 };
        try
        {
            window.Show();
            void AssertStatus(string expected)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var card = window.GetVisualDescendants().OfType<Border>().Single(border =>
                    border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
                var labels = card.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
                Assert.Contains(expected, labels);
                Assert.DoesNotContain(expected == "已部署" ? "未部署" : "已部署", labels);
                Assert.DoesNotContain("已启用", labels);
                Assert.DoesNotContain("已停用", labels);
            }

            mod.Enabled = true;
            vm.ApplyDeploymentState(new HashSet<Guid>());
            AssertStatus("未部署");
            vm.ApplyDeploymentState(new HashSet<Guid> { mod.ModId });
            AssertStatus("已部署");
            mod.Enabled = false;
            AssertStatus("已部署");
            vm.ApplyDeploymentState(new HashSet<Guid>());
            AssertStatus("未部署");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ActiveProfileDeploymentStatus_ShouldIgnoreDisabledModsAndFollowDeploymentRecords()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profileService = scope.Services.GetRequiredService<ProfileService>();
        var activeProfile = (await profileService.GetAllProfilesAsync()).First(profile => profile.IsActive);
        var allIds = activeProfile.Mods.Select(mod => mod.ModId).ToHashSet();
        Assert.True(allIds.Count >= 2);
        // 停用其中一个：它本就不参与部署，只部署其余启用模组仍应判为“已部署”。
        var disabledId = allIds.First();
        await profileService.SetModsEnabledAsync(activeProfile.Id, new[] { disabledId }, false);
        var enabledIds = allIds.Where(id => id != disabledId).ToHashSet();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;

        // 标签由后台读取回填，等待值变化而不是固定延时，避免慢机器上的偶发失败。
        async Task WaitFor(string expected)
        {
            for (var attempt = 0; attempt < 300 && vm.ActiveProfileDeploymentStatus != expected; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Assert.Equal(expected, vm.ActiveProfileDeploymentStatus);
        }

        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        try
        {
            window.Show();
            string StatusBarText()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var bar = window.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "StatusBar");
                return string.Concat(bar.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            }

            await WaitFor("Not Deployed");
            Assert.Contains(UiLocalization.Current["Shell.NotDeployed"], StatusBarText());
            vm.ApplyDeploymentState(enabledIds);
            await WaitFor("Deployed");
            Assert.Contains(UiLocalization.Current["UI.Deployed"], StatusBarText());
            Assert.DoesNotContain(UiLocalization.Current["Shell.NotDeployed"], StatusBarText());
            vm.ApplyDeploymentState(new HashSet<Guid>());
            await WaitFor("Not Deployed");
            vm.ApplyDeploymentState(new HashSet<Guid> { enabledIds.First() });
            await WaitFor("Partially Deployed");
            Assert.Contains(UiLocalization.Current["Shell.PartiallyDeployed"], StatusBarText());
        }
        finally { window.Close(); }

        // 排空初始化排队的后台刷新：在途 SQLite 连接未释放时 UiScope 删除临时库会抛 IOException。
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
