using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task IasSystem_LocationDiscoveryDeployUninstallAndTranslate()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>(); await language.SetLanguageAsync("zh-CN");

        // 导入一个 IAS 模型组件（HD2IR 运行包）。
        var modsPath = scope.Services.GetRequiredService<StorageSettingsService>().Current.ModsPath;
        var import = new ModImportService(scope.Services.GetRequiredService<IModRepository>(), modsPath,
            tempDirectory: Path.Combine(scope.Root, "temp"));
        var rigDir = Path.Combine(scope.Root, "rig"); Directory.CreateDirectory(rigDir);
        await File.WriteAllTextAsync(Path.Combine(rigDir, "hero.rigbin"), "rig");
        var archive = Path.Combine(scope.Root, "Hero.hd2irpack.zip");
        ZipFile.CreateFromDirectory(rigDir, archive);
        await import.ImportModAsync(archive);

        // 伪造 IAS 系统（HD2IntegratedArmorSystem）安装位置：上游安装器已建好 Runtime 目录。
        var location = Path.Combine(scope.Root, "ias");
        Directory.CreateDirectory(Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1"));
        var vm = main.IasSystemViewModel!;
        Assert.False(vm.CanUninstall);

        vm.AcceptLocation(location);
        await vm.RefreshCommand.ExecutionTask!;
        Assert.Equal(location, vm.Location);
        Assert.Equal(location, await scope.Services.GetRequiredService<IIasSystemService>().GetInstallLocationAsync());
        Assert.Equal(["rt-1"], vm.RuntimeIds);
        Assert.Equal("rt-1", vm.SelectedRuntimeId);
        Assert.True(vm.HasComponents);
        Assert.True(vm.CanDeploy);

        // 部署到 armor\rig，不写游戏 bin。
        await vm.DeployCommand.ExecuteAsync(null);
        var rigFile = Path.Combine(location, "HD2IntegratedArmorSystem", "Runtime", "rt-1", "armor", "rig", "hero.rigbin");
        Assert.Equal("rig", await File.ReadAllTextAsync(rigFile));
        Assert.Contains("已部署", vm.StatusMessage);
        Assert.True(vm.CanUninstall);
        Assert.Equal("", vm.CopyFallbackNotice);

        // 卸载删除部署文件，模组库不受影响。
        confirmation.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var uninstalling = vm.UninstallCommand.ExecuteAsync(null);
        Assert.True(confirmation.PendingConfirmation);
        Assert.Contains("armor\\rig", confirmation.Message);
        confirmation.Pending.SetResult(true);
        await uninstalling;
        Assert.False(File.Exists(rigFile));
        Assert.False(vm.CanUninstall);
        Assert.Contains("已卸载", vm.StatusMessage);

        // 语言切换刷新文案（无窗口环境直接断言 VM 派生状态）。
        await language.SetLanguageAsync("en-US");
        Assert.DoesNotContain("IasSystem.", vm.StatusMessage);
        await language.SetLanguageAsync("zh-TW");
        Assert.DoesNotContain("IasSystem.", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task IasSystem_CategoryShowsViewAndBindsViewModel()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>(); await language.SetLanguageAsync("zh-CN");
        main.SelectedTabIndex = 3;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout();
            var more = window.GetVisualDescendants().OfType<MoreView>().Single();
            _ = more.GetVisualDescendants().OfType<ListBox>().Single(box => ReferenceEquals(box.ItemsSource, main.MoreViewModel.Categories));
            // 末位分类在 960x600 视口外且容器未实现，直接走 VM 分类切换（控件选中绑定随之刷新）。
            main.MoreViewModel.SelectedCategory = "HD2IAS 系统";
            Layout();
            Assert.True(main.MoreViewModel.IsIasSystemVisible);
            var view = window.GetVisualDescendants().OfType<IasSystemView>().Single();
            Assert.Same(main.IasSystemViewModel, view.DataContext);
            Assert.Equal("HD2IAS 系统部署", view.FindControl<TextBlock>("IasSystemTitle")!.Text);
            // 模型组件与操作日志一般用不到，默认全部折叠展示。
            Assert.False(view.FindControl<Expander>("ModelsExpander")!.IsExpanded);
            Assert.False(view.FindControl<Expander>("OperationLogExpander")!.IsExpanded);
            await language.SetLanguageAsync("en-US"); Layout();
            Assert.Equal("HD2IAS system deployment", view.FindControl<TextBlock>("IasSystemTitle")!.Text);
            await language.SetLanguageAsync("zh-CN");
            var app = Avalonia.Application.Current!; var previousTheme = app.RequestedThemeVariant;
            app.RequestedThemeVariant = ThemeVariant.Dark; Layout();
            Capture(window, "Dark", "ias-system-zh-CN");
            app.RequestedThemeVariant = previousTheme;
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
