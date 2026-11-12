using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
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
    public async Task HellForge_ShouldFollowGameSourceAutoNavigateAndManageProfiles()
    {
        await using var scope = await UiScope.CreateAsync();
        using var fixture = HellForgeCliFixture.Create();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Button button)
            {
                Layout();
                // 成功通知的 toast 悬浮在右上角，会挡住页面按钮；点击前先收起。
                main.NotificationPanelViewModel.DismissAllToasts();
                Layout();
                Assert.True(button.IsEffectivelyEnabled);
                var point = button.TranslatePoint(new Point(10, 8), window)!.Value;
                var hit = window.InputHitTest(point) as Visual;
                Assert.True(button.IsVisualAncestorOf(hit) || ReferenceEquals(hit, button),
                    $"点击 {button.Name} 命中了 {hit?.GetType().Name ?? "null"}（点 {point}）");
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            Layout();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            var hfItem = navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "7");

            // 官方源下入口隐藏；Ctrl+D8 快捷键已挂到导航命令。
            Assert.False(hfItem.IsVisible);
            Assert.False(main.SettingsViewModel.ShowHellForgeEntry);
            Assert.Contains(window.KeyBindings, binding => binding.Gesture?.Key == Key.D8
                && binding.Gesture.KeyModifiers == KeyModifiers.Control
                && ReferenceEquals(binding.Command, main.NavigateToHellForgeCommand));

            // 设置切换到 HellForge 并保存：入口自动出现并导航到专页，页面经 CLI 替身加载。
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            Layout();
            Assert.True(main.SettingsViewModel.ShowHellForgeEntry);
            Assert.True(hfItem.IsVisible);
            Assert.Equal(7, main.SelectedTabIndex);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            var view = window.GetVisualDescendants().OfType<HellForgeView>().Single();
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(hf.HasStatus);
            Assert.Equal("0.4.0-beta", view.FindControl<TextBlock>("HellForgeVersionText")!.Text);
            Assert.Equal("default", hf.CurrentProfile);
            var rows = view.FindControl<ItemsControl>("ProfileList")!.GetVisualDescendants()
                .OfType<Border>().Where(border => border.Classes.Contains("card") && border.DataContext is HellForgeProfileItem).ToList();
            Assert.Equal(2, rows.Count);
            var defaultRow = rows.Single(row => ((HellForgeProfileItem)row.DataContext!).Name == "default");
            var altRow = rows.Single(row => ((HellForgeProfileItem)row.DataContext!).Name == "alt");
            Assert.Contains(defaultRow.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("chip") && border.IsEffectivelyVisible);
            Assert.DoesNotContain(altRow.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("chip") && border.IsEffectivelyVisible);

            // 点击 alt 行的“切换”：settings --profile alt 实际发出。
            Click(altRow.GetVisualDescendants().OfType<Button>().Single());
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.Contains("settings --profile alt", fixture.ReadArgs());

            // 新建档案：非法名被拦下且不触达 CLI；合法名创建成功并关闭弹窗。
            Click(view.FindControl<Button>("CreateProfileButton")!);
            Assert.True(hf.IsCreateDialogOpen);
            var nameInput = view.FindControl<TextBox>("NewProfileNameInput")!;
            nameInput.Text = "Bad Name";
            Click(view.FindControl<Button>("ConfirmCreateProfileButton")!);
            Assert.True(hf.IsCreateDialogOpen);
            Assert.False(string.IsNullOrEmpty(hf.CreateError));
            Assert.DoesNotContain("--create", fixture.ReadArgs());
            nameInput.Text = "new-run";
            Click(view.FindControl<Button>("ConfirmCreateProfileButton")!);
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.False(hf.IsCreateDialogOpen);
            Assert.Contains("profiles --create new-run --template starter", fixture.ReadArgs());

            // 切回官方源：入口隐藏，不停留在已隐藏的专页。
            main.SettingsViewModel.IsOfficialGameSource = true;
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            Layout();
            Assert.False(hfItem.IsVisible);
            Assert.NotEqual(7, main.SelectedTabIndex);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task HellForge_ShouldStreamSessionProgressFromLibraryLaunchAndStop()
    {
        await using var scope = await UiScope.CreateAsync();
        using var fixture = HellForgeCliFixture.Create();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();

            // 配置 HellForge 源并保存，进入专页。
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();

            // 预置一个会停在“运行中”等待 stop 的会话流。
            fixture.SetPlayStream("""
                {"event":"started","runId":"uirun","run":"stub/uirun","pid":1,"profile":"default","war":"live","arguments":""}
                {"event":"status","status":"preparing","failure":null}
                {"event":"status","status":"patching","failure":null}
                {"event":"status","status":"observing","failure":null}
                """, hold: true);

            // 从模组库页发起启动：应自动导航到 HF 专页并显示进度。
            main.NavigateToLibraryCommand.Execute(null);
            Layout();
            Assert.Equal(1, main.SelectedTabIndex);
            await main.LibraryViewModel.LaunchGameCommand.ExecuteAsync(null);
            await WaitForAsync(() => main.SelectedTabIndex == 7 && hf.IsSessionRunning);
            Layout();
            var view = window.GetVisualDescendants().OfType<HellForgeView>().Single();
            // VM 标志翻转到按钮绑定传播之间有调度间隙；等实际断言的控件状态 settled 再断言，避免无头偶发。
            await WaitForAsync(() => view.FindControl<Button>("StopSessionButton")!.IsEffectivelyEnabled
                && !view.FindControl<Button>("StartSessionButton")!.IsEffectivelyEnabled);
            Layout();
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(hf.IsSessionActive);
            Assert.False(view.FindControl<Button>("StartSessionButton")!.IsEffectivelyEnabled);
            Assert.True(view.FindControl<Button>("StopSessionButton")!.IsEffectivelyEnabled);

            // 步骤条：运行中为第 5 格，前 4 格完成。
            var steps = view.FindControl<ItemsControl>("SessionStepBar")!.GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("step-circle")).ToList();
            Assert.Equal(5, steps.Count);
            Assert.Equal(4, steps.Count(b => b.Classes.Contains("done")));
            Assert.True(steps[4].Classes.Contains("active"));

            // 停止：会话收尾，步骤全部完成，启动按钮恢复可用。
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var stop = view.FindControl<Button>("StopSessionButton")!;
            var point = stop.TranslatePoint(new Point(10, 8), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitForAsync(() => !hf.IsSessionActive);
            Layout();
            Assert.Contains("stop --wait yes", fixture.ReadArgs());
            Assert.True(view.FindControl<Button>("StartSessionButton")!.IsEffectivelyEnabled);
            steps = view.FindControl<ItemsControl>("SessionStepBar")!.GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("step-circle")).ToList();
            Assert.Equal(5, steps.Count(b => b.Classes.Contains("done")));
        }
        finally { window.DataContext = null; window.Close(); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }
}