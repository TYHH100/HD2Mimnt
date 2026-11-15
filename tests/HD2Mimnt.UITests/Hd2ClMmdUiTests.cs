using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task Hd2ClMmd_TabBindsSourceControlsAndLocalizesLabels()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");

        main.SelectedTabIndex = 3;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 900 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                window.CaptureRenderedFrame()?.Dispose();
            }

            Layout();
            main.MoreViewModel.SelectedCategory = "HD2CL 角色库";
            Layout();
            var more = window.GetVisualDescendants().OfType<MoreView>().Single();
            var tabs = more.GetVisualDescendants().OfType<TabControl>()
                .Single(control => control.Items.Count == 2);
            tabs.SelectedIndex = 1;
            Layout();

            var view = more.GetVisualDescendants().OfType<Hd2ClMmdView>().Single();
            var vm = Assert.IsType<MmdAdaptationViewModel>(view.DataContext);
            Assert.Same(main.MmdAdaptationViewModel, vm);

            var source = view.FindControl<TextBox>("MmdSourceBox")!;
            vm.SourcePath = @"C:\packs\test.zip";
            Layout();
            Assert.Equal(vm.SourcePath, source.Text);
            Assert.True(view.FindControl<RadioButton>("MmdIdleLoopButton")!.IsChecked);
            Assert.False(view.FindControl<Button>("MmdConvertButton")!.IsEffectivelyEnabled);
            Assert.True(view.FindControl<Button>("MmdCheckButton")!.IsEffectivelyEnabled);
            Assert.Equal("检查内容", view.FindControl<Button>("MmdCheckButton")!.Content);
            Assert.Contains("请重新检查", view.FindControl<TextBlock>("MmdStatusText")!.Text);

            // 检查结果同步到选项/待机方式/备注与转换按钮。
            vm.ApplyInspection(new MmdAdaptationInspection("测试曲包", @"C:\packs\test.zip",
                [new MmdAdaptationOption("default", "完整模组", ["a.patch_0"])], "default", "once",
                MmdAdaptationStatus.Ready, [new MmdAdaptationTrack("mmd_1", "测试曲包", 30, 5.5, "once", 166,
                    true, true, ["1111111111111111"])], [], true));
            Layout();
            Assert.True(view.FindControl<RadioButton>("MmdIdleOnceButton")!.IsChecked);
            Assert.True(view.FindControl<Button>("MmdConvertButton")!.IsEffectivelyEnabled);
            Assert.Contains("0:05", view.FindControl<TextBlock>("MmdNotesText")!.Text);

            await language.SetLanguageAsync("en-US");
            Layout();
            Assert.Equal("Start conversion", view.FindControl<Button>("MmdConvertButton")!.Content);
            Capture(window, "Current", "hd2cl-mmd");
            await language.SetLanguageAsync("zh-CN");
            Layout();
            Assert.Equal("开始转换", view.FindControl<Button>("MmdConvertButton")!.Content);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }
}
