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
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task DirectDownloadTools_RealButtonsDownloadImportSwitchLanguageAndCancelOnExit()
    {
        var factory = new DirectDownloadTestFactory();
        await using var scope = await UiScope.CreateAsync(directDownloads: factory);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.DirectDownloadViewModel!;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "直链下载";
        vm.Link = "https://example.com/file.zip?token=session-only";
        vm.Destination = Path.Combine(scope.Root, "downloads");
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!; var oldTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<DirectDownloadView>().Single();
            Assert.Same(vm, view.DataContext);
            var only = view.FindControl<Button>("DirectDownloadOnlyButton")!;
            var import = view.FindControl<Button>("DirectDownloadImportButton")!;
            Assert.True(only.IsEnabled); Assert.True(import.IsEnabled);
            Click(only); await vm.DownloadOnlyCommand.ExecutionTask!; Layout();
            Assert.Equal("文件下载完成。", vm.StatusMessage);
            Assert.True(File.Exists(vm.SavedPath));
            Assert.Empty(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
            Assert.False(vm.RetryCommand.CanExecute(null));

            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code); app.RequestedThemeVariant = theme; Layout();
                Assert.True(view.FindControl<TextBox>("DirectDownloadLinkInput")!.Bounds.Width > 200);
                Assert.True(view.FindControl<TextBox>("DirectDownloadDestinationInput")!.Bounds.Width > 150);
                Assert.NotEqual("DirectDownload.Only", only.Content);
                Assert.Equal(code switch { "zh-TW" => "檔案下載完成。", "en-US" => "File download completed.", _ => "文件下载完成。" },
                    view.FindControl<TextBlock>("DirectDownloadStatus")!.Text);
                var scroll = view.GetVisualAncestors().OfType<ScrollViewer>().First();
                scroll.Offset = default; Layout();
                Capture(window, theme.ToString(), "direct-download-" + code);
            }
            await language.SetLanguageAsync("zh-CN");
            // Bring the action row into view after checking the narrow, scrollable page.
            import.BringIntoView(); Layout();
            Click(import); await vm.DownloadAndImportCommand.ExecutionTask!; Layout();
            Assert.Equal("模组已下载并导入模组库。", vm.StatusMessage);
            Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
            Assert.False(vm.HasSavedFile);
            Assert.Empty(Directory.GetDirectories(scope.Services.GetRequiredService<StorageSettingsService>().Current.TempPath,
                "direct-import-*", SearchOption.AllDirectories));

            factory.Block = true;
            var pending = vm.DownloadOnlyCommand.ExecuteAsync(null);
            await factory.Started.Task;
            Assert.True(vm.IsBusy); Assert.False(vm.CanEdit);
            main.MoreViewModel.SelectedCategory = "教程"; Layout();
            Assert.True(vm.IsBusy); // Downloads continue when navigating elsewhere.
            Assert.True(await main.CanCloseAsync()); await pending;
            Assert.True(factory.Cancelled); Assert.True(factory.Disposed);
            Assert.False(vm.IsBusy); Assert.True(vm.Completion.IsCompleted);
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaFact]
    public async Task DirectDownloadTools_CustomProxyFailureAndRetry_PreserveSettingsAndRefreshErrors()
    {
        var factory = new DirectDownloadTestFactory();
        await using var scope = await UiScope.CreateAsync(directDownloads: factory);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.DirectDownloadViewModel!;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "直链下载";
        vm.Link = "invalid"; vm.Destination = Path.Combine(scope.Root, "downloads");
        vm.ProxyMode = "自定义代理"; vm.ProxyAddress = "socks5://127.0.0.1:1080";
        vm.ProxyUserName = "session-user"; vm.ProxyPassword = "session-password";
        await vm.DownloadOnlyCommand.ExecuteAsync(null);
        Assert.Equal("下载失败。", vm.StatusMessage); Assert.False(vm.IsBusy);
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!; var oldTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<DirectDownloadView>().Single();
            view.FindControl<Expander>("DirectDownloadNetworkExpander")!.IsExpanded = true;
            Layout();
            Assert.True(view.FindControl<TextBox>("DirectDownloadProxyAddressInput")!.IsEffectivelyVisible);
            Assert.Equal('●', view.FindControl<TextBox>("DirectDownloadProxyPasswordInput")!.PasswordChar);
            foreach (var code in new[] { "zh-CN", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code); app.RequestedThemeVariant = theme; Layout();
                Assert.Equal(code == "en-US" ? "Download failed." : "下载失败。", view.FindControl<TextBlock>("DirectDownloadStatus")!.Text);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == (code == "en-US"
                    ? "Enter a valid HTTP or HTTPS download link." : "请输入有效的 HTTP 或 HTTPS 下载链接。"));
                var scroll = view.GetVisualAncestors().OfType<ScrollViewer>().First();
                scroll.Offset = new Vector(0, 260); Layout();
                Capture(window, theme.ToString(), "direct-download-proxy-" + code);
            }
            vm.Link = "https://example.com/mod.zip"; factory.FailOnce = true;
            await vm.DownloadOnlyCommand.ExecuteAsync(null);
            Assert.True(vm.RetryCommand.CanExecute(null)); Assert.False(vm.HasSavedFile);
            await vm.RetryCommand.ExecuteAsync(null);
            Assert.True(File.Exists(vm.SavedPath)); Assert.False(vm.RetryCommand.CanExecute(null));
            Assert.Equal(DownloadProxyMode.Custom, factory.LastProxy!.Mode);
            Assert.Equal("session-password", factory.LastProxy.Password);
            var settings = scope.Services.GetRequiredService<SettingsService>();
            Assert.Equal("socks5://127.0.0.1:1080", settings.GetSetting("DirectDownload.ProxyAddress", ""));
            Assert.DoesNotContain("session-password", System.Text.Json.JsonSerializer.Serialize(await settings.GetAllSettingsAsync()));
            vm.ImportDownloadedArchiveAsync = (_, _) => throw new OperationCanceledException();
            await vm.DownloadAndImportCommand.ExecuteAsync(null);
            Assert.StartsWith("下载已取消", vm.StatusMessage);
            Assert.False(vm.HasSavedFile); Assert.True(vm.RetryCommand.CanExecute(null));
            Assert.Empty(Directory.GetDirectories(scope.Services.GetRequiredService<StorageSettingsService>().Current.TempPath,
                "direct-import-*", SearchOption.AllDirectories));
        }
        finally { await vm.CancelAndWaitAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaFact]
    public async Task DirectDownloadTools_LoginRequired_UsesSystemBrowserAndImportsUserFileWithoutCredentials()
    {
        var factory = new DirectDownloadTestFactory { ResponseFailure = DirectDownloadResponseFailure.LoginRequired };
        await using var scope = await UiScope.CreateAsync(directDownloads: factory);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.DirectDownloadViewModel!;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "直链下载";
        vm.Link = "https://download.example.com/redirect?token=session-only";
        await vm.DownloadAndImportCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsBrowser); Assert.False(vm.IsBusy); Assert.Equal(0, vm.Progress);
        Assert.Empty(vm.ProgressText); Assert.Empty(vm.SavedPath);
        Assert.Empty(scope.WebsiteLauncher.Urls); // Only an explicit click opens the system browser.
        Assert.Empty(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<DirectDownloadView>().Single();
            var browser = view.FindControl<Button>("DirectDownloadBrowserButton")!;
            var point = browser.TranslatePoint(new Point(browser.Bounds.Width / 2, browser.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            Assert.Equal(vm.Link, Assert.Single(scope.WebsiteLauncher.Urls));
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await language.SetLanguageAsync(code); Layout();
                Assert.Equal(code switch { "en-US" => "Complete verification in your browser.", "zh-TW" => "需要在瀏覽器完成驗證。", _ => "需要在浏览器完成验证。" },
                    view.FindControl<TextBlock>("DirectDownloadStatus")!.Text);
                Assert.DoesNotContain("40100", view.FindControl<TextBlock>("DirectDownloadErrorDetail")!.Text!);
                var import = view.FindControl<Button>("DirectDownloadBrowserImportButton")!;
                import.BringIntoView(); Layout();
                Assert.True(import.IsEffectivelyVisible && import.IsEffectivelyEnabled);
                Assert.True(view.FindControl<TextBlock>("DirectDownloadBrowserHint")!.Bounds.Width > 200);
                Assert.True(import.Bounds.Width <= view.Bounds.Width);
            }
            var source = Path.Combine(scope.Root, "browser-download.zip");
            await factory.WriteArchiveAsync(source);
            await vm.ImportBrowserFileAsync(source); Layout();
            Assert.Equal("浏览器下载的文件已导入模组库。", vm.StatusMessage);
            Assert.Single(await scope.Services.GetRequiredService<ModService>().GetAllModsAsync());
            Assert.True(File.Exists(source)); Assert.False(vm.NeedsBrowser);
            var saved = System.Text.Json.JsonSerializer.Serialize(await scope.Services.GetRequiredService<SettingsService>().GetAllSettingsAsync());
            Assert.DoesNotContain("session-only", saved);
        }
        finally { await vm.CancelAndWaitAsync(); window.DataContext = null; window.Close(); }
    }

    private sealed class DirectDownloadTestFactory : IDirectDownloadClientFactory
    {
        private readonly byte[] _archive;
        public bool Block { get; set; }
        public bool FailOnce { get; set; }
        public DirectDownloadResponseFailure? ResponseFailure { get; set; }
        public DownloadProxyOptions? LastProxy { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DirectDownloadTestFactory()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var entry = archive.CreateEntry("1234567890123456.patch_0").Open())
                entry.Write(new byte[] { 1, 2, 3, 4 });
            _archive = stream.ToArray();
        }
        public IDirectDownloadClient Create(DownloadProxyOptions proxy) { LastProxy = proxy; return new Client(this); }
        public Task WriteArchiveAsync(string path) => File.WriteAllBytesAsync(path, _archive);
        private sealed class Client(DirectDownloadTestFactory owner) : IDirectDownloadClient
        {
            public Task<DirectDownloadInfo> GetInfoAsync(Uri uri, CancellationToken cancellationToken = default)
                => owner.ResponseFailure is { } failure ? throw new DirectDownloadResponseException(failure)
                    : Task.FromResult(new DirectDownloadInfo("测试模组.zip", owner._archive.Length));
            public async Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null,
                CancellationToken cancellationToken = default, string? resumeKey = null)
            {
                if (owner.FailOnce) { owner.FailOnce = false; throw new IOException("test interruption"); }
                if (owner.Block)
                {
                    owner.Started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { owner.Cancelled = true; throw; }
                }
                progress?.Report(new() { DownloadedBytes = owner._archive.Length, TotalBytes = owner._archive.Length, BytesPerSecond = 1024 });
                return new MemoryStream(owner._archive);
            }
            public async Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
                IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            {
                using var source = await DownloadAsync(uri, options, progress, cancellationToken, resumeKey);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var file = File.Create(targetPath); await source.CopyToAsync(file, cancellationToken);
            }
            public void Dispose() => owner.Disposed = true;
        }
    }
}
