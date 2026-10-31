using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task Hd2IAS_ToolsEntryInspectGenerateBothRoutesTranslateAndCancelOnExit()
    {
        var reader = new TestIASPackages(); var compiler = new TestIASCompiler(); var updates = new TestIASUpdates();
        await using var scope = await UiScope.CreateAsync(iasPackages: reader, iasCompiler: compiler, iasUpdates: updates);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>(); await language.SetLanguageAsync("zh-CN");
        var game = Path.Combine(scope.Root, "Helldivers 2"); Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(game, "data"));
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "helldivers2.exe"), "game");
        await File.WriteAllTextAsync(Path.Combine(game, "data", "bundle_database.data"), "database");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "lua51.dll"), "vm");
        Directory.CreateDirectory(Path.Combine(game, "bin", "HD2IndependentRig"));
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig.build.json"),
            "{\"schema\":\"HD2IndependentRigBuild1\",\"version\":\"1.4.3\",\"pose_route\":\"native\"}");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig", "physics_control.ini"),
            "HD2PhysicsControl1\nenabled=0");
        await scope.Services.GetRequiredService<GameDetectionService>().ApplyConfiguredPathAsync(game);
        var zip = Path.Combine(scope.Root, "source.zip"); reader.WriteSources(zip);
        updates.PackagePath = zip;
        main.SelectedTabIndex = 3;
        var vm = main.Hd2IASManagementViewModel!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!; var previousTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            void Click(Control control)
            {
                control.BringIntoView();
                Layout(); var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, ((Visual)window.InputHitTest(point)!).GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var more = window.GetVisualDescendants().OfType<MoreView>().Single();
            var categories = more.GetVisualDescendants().OfType<ListBox>().Single(box => ReferenceEquals(box.ItemsSource, main.MoreViewModel.Categories));
            Assert.Equal("HD2IAS 管理", main.MoreViewModel.Categories[^3]);
            Click((Control)categories.ContainerFromIndex(Array.IndexOf(main.MoreViewModel.Categories, "HD2IAS 管理"))!);
            await vm.RefreshInstallationCommand.ExecutionTask!; Layout();
            Assert.True(main.MoreViewModel.IsHd2IASVisible); Assert.False(main.MoreViewModel.IsAboutVisible);
            var view = window.GetVisualDescendants().OfType<Hd2IASManagementView>().Single();
            // 页内区块默认全部折叠，测试先展开全部 Expander 再进行点击交互。
            foreach (var expander in view.GetVisualDescendants().OfType<Expander>().ToList()) expander.IsExpanded = true; Layout();
            Assert.Same(vm, view.DataContext); Assert.True(view.IsEffectivelyVisible);
            Assert.Equal("native", vm.SelectedRoute);
            Assert.True(vm.HasInstallationReport); Assert.False(vm.HasReport); Assert.Equal("", vm.PackagePath);
            Assert.Contains("1.4.3", vm.InstalledBuildSummary); Assert.Contains("Native", vm.InstalledBuildSummary);
            Assert.Contains("保留", vm.ConfigurationSummary); Assert.Contains("physics_control.ini", vm.ConfigurationSummary);
            Assert.Contains("HD2IndependentRigBuild1", vm.InstalledBuildContents);
            Assert.Equal("native", view.FindControl<ComboBox>("Hd2IASRoute")!.SelectedItem);
            var inspect = view.FindControl<Button>("InspectHd2IASButton")!;
            var generate = view.FindControl<Button>("InstallHd2IASButton")!;
            Assert.False(inspect.IsEffectivelyEnabled); Assert.False(generate.IsEnabled);
            var refresh = view.FindControl<Button>("RefreshHd2IASVersionsButton")!;
            var download = view.FindControl<Button>("DownloadHd2IASUpdateButton")!;
            Assert.False(download.IsEffectivelyEnabled);
            Click(refresh); await vm.RefreshVersionsCommand.ExecutionTask!; Layout();
            Assert.Equal("1.4.4", vm.SelectedUpdate!.Version); Assert.True(download.IsEffectivelyEnabled);
            Click(download); await vm.DownloadUpdateCommand.ExecutionTask!; Layout();
            Assert.EndsWith("HD2IAS-1.4.4-update.zip", vm.PackagePath); Assert.NotEqual(zip, vm.PackagePath);
            Assert.True(vm.HasReport); Assert.True(generate.IsEnabled);
            Assert.Contains("1.1.3", vm.PackageSummary); Assert.Contains("native-v1", vm.PackageSummary);
            Guid? dataModId = null;
            foreach (var route in vm.Routes)
            {
                view.FindControl<ComboBox>("Hd2IASRoute")!.SelectedItem = route; Layout();
                if (!vm.HasReport) await vm.InspectCommand.ExecuteAsync(null);
                Click(generate); await vm.InstallCommand.ExecutionTask!; Layout();
                Assert.Contains("已安装", vm.StatusMessage);
                Assert.False(File.Exists(Path.Combine(game, "data", "9ba626afa44a3aa3.patch_0")));
                var dataMod = Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
                if (dataModId != null) Assert.Equal(dataModId, dataMod.Id);
                dataModId = dataMod.Id;
                Assert.True(File.Exists(Path.Combine(dataMod.Path, "data", "9ba626afa44a3aa3.patch_0")));
                Assert.Contains("模组库", vm.StatusMessage);
                Assert.Empty(Directory.GetFiles(scope.Root, "*.zip").Where(path => path != zip));
                var managed = await scope.Services.GetRequiredService<HD2Mimnt.Infrastructure.Hd2IAS.Hd2IASDeploymentStore>().ReadAsync(game);
                Assert.Equal(route == "native" ? Hd2IASRoute.Native : Hd2IASRoute.Lua, managed!.Route);
                Assert.Equal("HD2PhysicsControl1\nenabled=0", await File.ReadAllTextAsync(Path.Combine(game, "bin", "HD2IndependentRig", "physics_control.ini")));
            }
            Assert.Equal(2, compiler.Calls);
            Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
            foreach (var (code, title) in new[] { ("zh-CN", "HD2IAS 管理"), ("zh-TW", "HD2IAS 管理"), ("en-US", "HD2IAS management") })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code); app.RequestedThemeVariant = theme; Layout();
                Assert.Equal(title, view.FindControl<TextBlock>("Hd2IASTitle")!.Text);
                Assert.DoesNotContain("IAS.", vm.StatusMessage); Assert.Contains(scope.Root, vm.StatusMessage);
                Assert.True(view.FindControl<TextBox>("Hd2IASPackagePath")!.Bounds.Width > 150);
                Capture(window, theme.ToString(), "hd2ias-" + code);
            }
            updates.RateLimited = true;
            await vm.RefreshVersionsCommand.ExecuteAsync(null); Layout();
            Assert.Contains("too many requests", vm.StatusMessage);
            await language.SetLanguageAsync("zh-CN"); Assert.Contains("请求过于频繁", vm.StatusMessage);
            updates.RateLimited = false; updates.Block = true;
            vm.SelectedUpdate = vm.UpdateVersions.Single(version => version.Version == "1.4.3"); // 选择未缓存版本，验证真实等待下载的取消。
            var downloading = vm.DownloadUpdateCommand.ExecuteAsync(null);
            await updates.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.IsBusy); Assert.False(refresh.IsEffectivelyEnabled); Assert.False(vm.CanGenerate);
            await vm.CancelAndWaitAsync(); await downloading; Layout();
            Assert.False(vm.IsBusy); Assert.Contains("取消", vm.StatusMessage);
            vm.PackagePath = zip + ".changed"; Layout(); Assert.False(vm.HasReport); Assert.True(vm.CanInstall);
            reader.Block = true; var pending = vm.InspectCommand.ExecuteAsync(null);
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.IsBusy); Assert.False(vm.CanEdit);
            Assert.True(await main.CanCloseAsync()); await pending;
            Assert.True(vm.Completion.IsCompleted); Assert.False(vm.IsBusy); Assert.False(vm.HasReport);
        }
        finally
        {
            await vm.CancelAndWaitAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public async Task Hd2IAS_MissingDataShowsBannerAndRepairsSameEntryWithoutNotification()
    {
        var reader = new TestIASPackages();
        await using var scope = await UiScope.CreateAsync(iasPackages: reader, iasCompiler: new TestIASCompiler(), iasUpdates: new TestIASUpdates());
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>(); await language.SetLanguageAsync("zh-CN");
        var game = Path.Combine(scope.Root, "game"); Directory.CreateDirectory(Path.Combine(game, "bin")); Directory.CreateDirectory(Path.Combine(game, "data"));
        foreach (var (path, content) in new[] { ("bin/helldivers2.exe", "game"), ("bin/lua51.dll", "vm"), ("data/bundle_database.data", "database"),
            ("bin/HD2IndependentRig.addon64", "Native-addon"),
            ("bin/HD2IndependentRig.build.json", "{\"schema\":\"HD2IndependentRigBuild1\",\"version\":\"1.4.4\",\"pose_route\":\"native\"}") })
            await File.WriteAllTextAsync(Path.Combine(game, path), content);
        await scope.Services.GetRequiredService<GameDetectionService>().ApplyConfiguredPathAsync(game);
        var vm = main.Hd2IASManagementViewModel!; await vm.CheckRuntimeRequirementAsync();
        Assert.True(vm.NeedsDataGeneration); Assert.False(vm.NeedsRuntimeInstallation);
        var notifications = scope.Services.GetRequiredService<NotificationService>();
        // 运行时缺失自动提醒暂时停用：检查到缺失不再弹通知，只靠页面内警示卡。
        Assert.DoesNotContain(notifications.GetRecentNotifications(), item => item.Message.Contains("data 缺失"));
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "HD2IAS 管理";
        Assert.Equal(3, main.SelectedTabIndex); Assert.True(main.MoreViewModel.IsHd2IASVisible);
        var zip = Path.Combine(scope.Root, "source.zip"); reader.WriteSources(zip); vm.PackagePath = zip;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<Hd2IASManagementView>().Single();
            var banner = view.FindControl<Border>("Hd2IASDataRequired")!;
            Assert.True(banner.IsEffectivelyVisible);
            foreach (var code in new[] { "en-US", "zh-TW", "zh-CN" })
            {
                await language.SetLanguageAsync(code); Layout();
                Assert.Equal(language["IAS.DataRequired"], Assert.IsType<TextBlock>(banner.Child).Text);
                Assert.DoesNotContain("IAS.", vm.DataRequiredMessage);
            }
            await vm.InstallCommand.ExecuteAsync(null); Layout();
            Assert.Contains("已安装", vm.StatusMessage); Assert.False(vm.NeedsDataGeneration); Assert.False(banner.IsVisible);
            var mod = Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
            notifications.Clear(); // 清理部署成功后的常规通知，专注验证缺失重检。
            File.Delete(Path.Combine(mod.Path, "data", "9ba626afa44a3aa3.patch_0"));
            await vm.RefreshInstallationCommand.ExecuteAsync(null); Layout();
            Assert.True(banner.IsEffectivelyVisible); Assert.True(vm.NeedsDataGeneration);
            Assert.DoesNotContain(notifications.GetRecentNotifications(), item => item.Message.Contains("data 缺失"));
            await vm.CheckRuntimeRequirementAsync();
            Assert.DoesNotContain(notifications.GetRecentNotifications(), item => item.Message.Contains("data 缺失"));
            await vm.InstallCommand.ExecuteAsync(null); Layout();
            Assert.Contains("已安装", vm.StatusMessage); Assert.False(vm.NeedsDataGeneration); Assert.False(banner.IsVisible);
            Assert.Equal(mod.Id, Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).Id);
            Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
        }
        finally { await main.CanCloseAsync(); window.DataContext = null; window.Close(); }
    }

    private sealed class TestIASUpdates : IHd2IASUpdateSource
    {
        public string PackagePath = "";
        public bool RateLimited, Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Hd2IASUpdateVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RateLimited) throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
            return Task.FromResult<IReadOnlyList<Hd2IASUpdateVersion>>([new("1.4.4"), new("1.4.3")]);
        }
        public async Task<Stream> DownloadAsync(Hd2IASUpdateVersion version,
            IProgress<HD2Mimnt.Core.Entities.DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return File.OpenRead(PackagePath);
        }
    }

    private sealed class TestIASCompiler : IHd2IASLuaCompiler
    {
        public int Calls;
        public Task ValidateAsync(string path, string hash, byte[] source, string module, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; }
    }

    private sealed class TestIASPackages : IHd2IASPackageReader
    {
        public bool Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private const string Body = "local value = 1";
        private const string Template = "-- HD2-Addon: mods/hd2ir/runtime\nlocal identity = '@@MODULE_IDENTITY@@'\n@@RUNTIME_BODY@@\n";
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public async Task<Hd2IASPackage> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            Hd2IASRuntimeManifest Route(Hd2IASRoute route) => new("1.4.4", route, "native-v1", Hash("game"), Hash("database"), Hash("vm"),
                new("mods/hd2ir/runtime", "70217982548cf034", Hash(Body), Hash(route.ToString()),
                    Hash(Template.Replace("@@MODULE_IDENTITY@@", Hash(route.ToString())).Replace("@@RUNTIME_BODY@@", Body))),
                [new("runtime.lua", null, Hash(Body), null, []), new("independent_loader.lua", null, Hash(Template), null, []),
                    new("addon64", "bin/HD2IndependentRig.addon64", Hash(route.ToString() + "-addon"), null, []),
                    new("dxgi.dll", "bin/dxgi.dll", Hash("dxgi"), null, [])], []);
            return new("1.4.4", Route(Hd2IASRoute.Lua), Route(Hd2IASRoute.Native),
                new("1.1.3", Hash("game"), Hash("database"), []), [new("runtime.lua", Hd2IASFileState.Match)], "test notes", []);
        }
        public void WriteSources(string path)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var prefix in new[] { "RuntimePackage/", "RuntimePackage/Native/" })
            foreach (var (name, content) in new[] { ("runtime.lua", Body), ("independent_loader.lua", Template) })
            { using var entry = archive.CreateEntry(prefix + name).Open(); entry.Write(Encoding.UTF8.GetBytes(content)); }
            foreach (var (prefix, route) in new[] { ("RuntimePackage/", "Lua"), ("RuntimePackage/Native/", "Native") })
            foreach (var (name, content) in new[] { ("addon64", route + "-addon"), ("dxgi.dll", "dxgi") })
            { using var entry = archive.CreateEntry(prefix + name).Open(); entry.Write(Encoding.UTF8.GetBytes(content)); }
        }
    }

    [AvaloniaFact]
    public async Task Hd2IAS_MissingRuntimeNavigatesAndUninstallPreservesReshadeAndData()
    {
        var reader = new TestIASPackages(); var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(iasPackages: reader, iasCompiler: new TestIASCompiler(),
            iasUpdates: new TestIASUpdates(), confirmation: confirmation);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        await scope.Services.GetRequiredService<ILocalizationService>().SetLanguageAsync("zh-CN");
        var game = Path.Combine(scope.Root, "game"); Directory.CreateDirectory(Path.Combine(game, "bin")); Directory.CreateDirectory(Path.Combine(game, "data"));
        foreach (var (path, content) in new[] { ("bin/helldivers2.exe", "game"), ("bin/lua51.dll", "vm"), ("data/bundle_database.data", "database") })
            await File.WriteAllTextAsync(Path.Combine(game, path), content);
        await scope.Services.GetRequiredService<GameDetectionService>().ApplyConfiguredPathAsync(game);
        var service = scope.Services.GetRequiredService<Hd2IASIntegrationService>();
        var zip = Path.Combine(scope.Root, "source.zip"); reader.WriteSources(zip);
        var exported = await service.GenerateAsync(zip, game, Hd2IASRoute.Native, Path.Combine(scope.Root, "legacy-data.zip"));
        await scope.Services.GetRequiredService<ModService>().ImportModAsync(exported.Path);
        var vm = main.Hd2IASManagementViewModel!; await vm.CheckRuntimeRequirementAsync();
        Assert.True(vm.NeedsRuntimeInstallation);
        var notifications = scope.Services.GetRequiredService<NotificationService>();
        // 运行时缺失自动提醒暂时停用：不再弹通知，直接导航进页面。
        Assert.DoesNotContain(notifications.GetRecentNotifications(), item => item.Message.Contains("运行时缺失"));
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "HD2IAS 管理";
        Assert.Equal(3, main.SelectedTabIndex); Assert.True(main.MoreViewModel.IsHd2IASVisible);
        vm.PackagePath = zip;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            void Click(Control control)
            {
                main.NotificationPanelViewModel.DismissAllToasts(); control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, ((Visual)window.InputHitTest(point)!).GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout();
            var view = window.GetVisualDescendants().OfType<Hd2IASManagementView>().Single();
            // 页内区块默认全部折叠，测试先展开全部 Expander 再进行点击交互。
            foreach (var expander in view.GetVisualDescendants().OfType<Expander>().ToList()) expander.IsExpanded = true; Layout();
            Assert.True(view.FindControl<Border>("Hd2IASRuntimeRequired")!.IsEffectivelyVisible);
            Click(view.FindControl<Button>("InstallHd2IASButton")!); await vm.InstallCommand.ExecutionTask!;
            await vm.CheckRuntimeRequirementAsync(); Layout();
            Assert.Contains("已安装", vm.StatusMessage); Assert.False(vm.NeedsRuntimeInstallation); Assert.True(vm.CanUninstall);
            Assert.False(view.FindControl<Border>("Hd2IASRuntimeRequired")!.IsVisible);
            var dataId = Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).Id;
            var dxgi = Path.Combine(game, "bin", "dxgi.dll"); await File.WriteAllTextAsync(dxgi, "reshade-external");
            confirmation.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(view.FindControl<Button>("UninstallHd2IASButton")!);
            Assert.True(confirmation.PendingConfirmation); confirmation.Pending.SetResult(false);
            await vm.UninstallCommand.ExecutionTask!;
            Assert.True(File.Exists(Path.Combine(game, "bin", "HD2IndependentRig.addon64")));
            confirmation.Pending = null;
            Click(view.FindControl<Button>("UninstallHd2IASButton")!); await vm.UninstallCommand.ExecutionTask!; Layout();
            Assert.Contains("dxgi.dll", confirmation.Message);
            Assert.Contains("已卸载", vm.StatusMessage); Assert.True(vm.NeedsRuntimeInstallation); Assert.False(vm.CanUninstall);
            Assert.Equal("reshade-external", await File.ReadAllTextAsync(dxgi));
            Assert.False(File.Exists(Path.Combine(game, "bin", "HD2IndependentRig.addon64")));
            Assert.Equal(dataId, Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).Id);
            Assert.Empty(Directory.GetFiles(Path.Combine(game, "data"), "*.patch_*"));
        }
        finally { await main.CanCloseAsync(); window.DataContext = null; window.Close(); }
    }
}
