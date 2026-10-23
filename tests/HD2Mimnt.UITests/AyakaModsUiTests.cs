using System.Net;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task AyakaApiDownloadToggle_ShouldSwitchBrowseSaveImportAndErrorWithoutBrowserFallback()
    {
        using var ayaka = new AyakaUiHandler();
        await using var scope = await UiScope.CreateAsync(ayakaHandler: ayaka, ayakaDownloadHandler: ayaka);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<AyakaModsAuthService>().ApplyApiKeyAsync("synthetic-ui-ayaka-key");
        var vm = main.BrowseModsViewModel!;
        vm.SelectedPlatform = vm.Platforms.Single(p => p.Id == "ayakamods"); await vm.PlatformChange;
        vm.ModLink = "42"; await vm.OpenLinkCommand.ExecuteAsync(null);
        var view = new BrowseModsView { DataContext = vm };
        var window = new Window { Content = view, Width = 960, Height = 740 };
        var dev = new DevToolsWindow
        {
            DataContext = new DevToolsViewModel(scope.Services.GetRequiredService<TestDataService>(),
                scope.Services.GetRequiredService<ProfileService>(), scope.Services.GetRequiredService<NexusModsTestService>())
            { AyakaAccount = main.SettingsViewModel.AyakaAccount, BrowseMods = vm }
        };
        try
        {
            void Layout(Window owner)
            { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); using var frame = owner.CaptureRenderedFrame(); }
            window.Show(); dev.Show(); Layout(window); Layout(dev);
            var toggle = dev.FindControl<CheckBox>("AyakaApiDownloadToggle")!;
            void Toggle()
            {
                toggle.BringIntoView(); Layout(dev);
                var point = toggle.TranslatePoint(new Point(12, toggle.Bounds.Height / 2), dev)!.Value;
                Assert.True(toggle.IsEffectivelyEnabled);
                Assert.Same(toggle, (dev.InputHitTest(point) as Control)?.FindAncestorOfType<CheckBox>(includeSelf: true));
                dev.MouseDown(point, MouseButton.Left); dev.MouseUp(point, MouseButton.Left);
                Layout(dev); Layout(window);
            }
            Assert.False(vm.UseAyakaApiDownload); Assert.True(vm.UseBrowserDownload);
            Assert.False(toggle.IsChecked);
            Toggle();
            Assert.True(vm.UseAyakaApiDownload); Assert.False(vm.UseBrowserDownload);
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                var localization = scope.Services.GetRequiredService<ILocalizationService>();
                await localization.SetLanguageAsync(language); dev.Width = 480; Layout(dev); Layout(window);
                Assert.Equal(localization["Ayaka.ApiDownloadToggle"], toggle.Content);
                Assert.Equal(localization["Browse.Download"], view.FindControl<Button>("BrowseDownloadButton")!.Content);
                Assert.Equal(localization["Ayaka.ApiDownloadHint"], view.FindControl<TextBlock>("BrowseDownloadHint")!.Text);
            }
            var destination = Path.Combine(scope.Root, "api.zip");
            await vm.DownloadAsync(destination).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("test", await File.ReadAllTextAsync(destination));
            Assert.Contains("/api/mod-versions/9/download?file=10", ayaka.Paths);
            Assert.False(vm.IsWaitingForBrowser || vm.IsWaitingForWebsite);
            var importing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ImportDownloadedArchiveAsync = async (path, token) =>
            { importing.SetResult(path); await finishImport.Task.WaitAsync(token); };
            var pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
            var source = await importing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                Assert.Equal("test", await File.ReadAllTextAsync(source));
                Layout(dev); Assert.False(toggle.IsEffectivelyEnabled);
                window.Content = new Border();
                await vm.LeavePageAsync();
                Assert.True(vm.IsBusy);
                Assert.False(pending.IsCompleted);
            }
            finally { finishImport.TrySetResult(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
            window.Content = view;
            Assert.False(File.Exists(source));
            Assert.False(Directory.Exists(Path.GetDirectoryName(source)));
            ayaka.FailDownload = true;
            var failed = Path.Combine(scope.Root, "failed.zip");
            await vm.DownloadAsync(failed).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("400", vm.Message);
            Assert.False(File.Exists(failed));
            await main.TaskCenterViewModel.LoadAsync();
            Assert.Contains(main.TaskCenterViewModel.Tasks, task => task.Type == "PlatformDownload" && task.Status == HD2Mimnt.Core.Entities.TaskStatus.Completed);
            Assert.Contains(main.TaskCenterViewModel.Tasks, task => task.Type == "PlatformDownload" && task.Status == HD2Mimnt.Core.Entities.TaskStatus.Failed);
            Assert.Empty(scope.WebsiteLauncher.Urls);
            Assert.False(vm.IsWaitingForBrowser || vm.IsWaitingForWebsite);
            Toggle(); Assert.False(vm.UseAyakaApiDownload);
            Assert.True(vm.UseBrowserDownload);
            Assert.Equal(scope.Services.GetRequiredService<ILocalizationService>()["BrowserDownload.Open"], view.FindControl<Button>("BrowseDownloadButton")!.Content);
            await vm.DownloadAsync(Path.Combine(scope.Root, "browser-unused.zip"));
            Assert.Equal("https://ayakamods.com/mods/42/download", Assert.Single(scope.WebsiteLauncher.Urls));
        }
        finally { await vm.CloseAsync(); dev.Close(); window.Content = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task AyakaBrowserUpdate_ShouldCancelAndThenReplaceFromChosenFileWithoutApiDownload()
    {
        using var ayaka = new AyakaUiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, ayakaHandler: ayaka);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await scope.Services.GetRequiredService<AyakaModsAuthService>().ApplyApiKeyAsync("synthetic-ui-ayaka-key");
        var path = Path.Combine(scope.Root, "browser-update.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("0123456789abcdef.patch_0").Open())) writer.Write("browser update patch");
        ayaka.FileSize = new FileInfo(path).Length;
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        var links = scope.Services.GetRequiredService<ModLinkService>();
        await links.SaveAsync(mod.Id, await links.FetchAsync("https://ayakamods.com/mods/42/"));
        var bound = (await repository.GetByIdAsync(mod.Id))!;
        var online = main.LibraryViewModel.ModOnline!;
        var window = new Window { Content = new LibraryView { DataContext = main.LibraryViewModel }, Width = 960, Height = 740 };
        try
        {
            window.Show(); main.NotificationPanelViewModel.DismissAllToasts();
            await online.OpenAsync(new ModItemViewModel(bound));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(online.CanDownload, online.Message);
            var pending = online.UpdateCommand.ExecuteAsync(null);
            await WaitForAyakaBrowserAsync(() => online.IsWaitingForBrowser, () => online.Message);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using (var frame = window.CaptureRenderedFrame()) { }
            var chooser = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OnlineChooseBrowserFileButton");
            Assert.True(chooser.IsEffectivelyVisible && chooser.IsEffectivelyEnabled);
            await online.CloseCommand.ExecuteAsync(null);
            Assert.False(online.IsOpen);
            Assert.True(online.IsWaitingForBrowser && online.IsBusy);
            await online.StopAsync(); await pending;
            Assert.False(online.IsWaitingForBrowser || online.IsBusy);
            Assert.Equal(bound.Version, (await repository.GetByIdAsync(mod.Id))!.Version);
            await online.OpenAsync(new ModItemViewModel(bound));
            pending = online.UpdateCommand.ExecuteAsync(null);
            await WaitForAyakaBrowserAsync(() => online.IsWaitingForBrowser, () => online.Message);
            Assert.False(online.IsWaitingForWebsite);
            Assert.Equal("https://ayakamods.com/mods/42/download", scope.WebsiteLauncher.Urls.Last());
            await online.SelectBrowserFileAsync(path);
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
            var updated = (await repository.GetByIdAsync(mod.Id))!;
            Assert.Equal("2.0", updated.Version);
            Assert.Equal(bound.Name, updated.Name);
            Assert.Equal("ayaka-file:9:10", updated.PlatformLink!.InstalledFileVersionId);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(ayaka.Paths, p => p.Contains("/download"));
        }
        finally { await online.StopAsync(); window.Content = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task AyakaBrowserDownload_ShouldImportAutomaticOrManualFileAndSupportExplicitCancellation()
    {
        using var ayaka = new AyakaUiHandler();
        await using var scope = await UiScope.CreateAsync(ayakaHandler: ayaka);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<AyakaModsAuthService>().ApplyApiKeyAsync("synthetic-ui-ayaka-key");
        var vm = main.BrowseModsViewModel!;
        vm.SelectedPlatform = vm.Platforms.Single(p => p.Id == "ayakamods");
        await vm.PlatformChange;
        vm.ModLink = "42";
        await vm.OpenLinkCommand.ExecuteAsync(null);
        var imports = new List<string>();
        var sources = new List<ModPlatformLink>();
        vm.ImportPlatformArchiveAsync = (path, source, _) => { imports.Add(path); sources.Add(source); return Task.CompletedTask; };
        var downloads = scope.Services.GetRequiredService<BrowserDownloadHandoffService>();
        Directory.CreateDirectory(downloads.DownloadsDirectory);
        var window = new Window { Content = new BrowseModsView { DataContext = vm }, Width = 960, Height = 740 };
        try
        {
            window.Show();
            var view = (BrowseModsView)window.Content!;
            void Layout()
            { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                var localization = scope.Services.GetRequiredService<ILocalizationService>();
                await localization.SetLanguageAsync(language);
                foreach (var platform in new[] { "nexusmods", "ayakamods" })
                {
                    vm.SelectedPlatform = vm.Platforms.Single(choice => choice.Id == platform);
                    await vm.PlatformChange;
                    Layout();
                    Assert.Equal(localization[platform == "ayakamods" ? "Ayaka.SelectHint" : "Browse.SelectHint"],
                        view.FindControl<TextBlock>("BrowseSelectHint")!.Text);
                    Assert.False(vm.HasDetails);
                }
            }
            vm.ModLink = "https://ayakamods.com/mods/armor.42/download";
            await vm.OpenLinkCommand.ExecuteAsync(null);
            Layout();
            vm.OpenBrowserDownload();
            Assert.False(vm.IsWaitingForBrowser);
            Assert.Equal("https://ayakamods.com/mods/42/download", scope.WebsiteLauncher.Urls.Last());
            foreach (var automatic in new[] { true, false })
            {
                var pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
                await WaitForAyakaBrowserAsync(() => vm.IsWaitingForBrowser);
                Assert.False(vm.IsWaitingForWebsite);
                Assert.Equal("https://ayakamods.com/mods/42/download", scope.WebsiteLauncher.Urls.Last());
                Layout();
                var chooser = view.FindControl<Button>("BrowseChooseBrowserFileButton")!;
                Assert.True(chooser.IsEffectivelyVisible && chooser.IsEffectivelyEnabled);
                foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
                {
                    var localization = scope.Services.GetRequiredService<ILocalizationService>();
                    await localization.SetLanguageAsync(language); Layout();
                    Assert.Equal(localization["BrowserDownload.ChooseFile"], chooser.Content);
                    Assert.Equal(localization["Ayaka.OpenWebsite"], view.FindControl<Button>("BrowseOpenWebsiteButton")!.Content);
                    Assert.Equal(localization["BrowserDownload.Open"], view.FindControl<Button>("BrowseDownloadButton")!.Content);
                }
                var path = Path.Combine(automatic ? downloads.DownloadsDirectory : scope.Root, automatic ? "armor.zip" : "renamed.zip");
                await File.WriteAllTextAsync(path, "test");
                if (!automatic) await vm.SelectBrowserFileAsync(path);
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(path, imports.Last());
                Assert.Equal("ayakamods", sources.Last().PlatformId);
                Assert.Equal("42", sources.Last().ModId);
                Assert.Equal(vm.SelectedFile!.Id, sources.Last().InstalledFileVersionId);
                Assert.Equal(vm.SelectedFile.Version, sources.Last().InstalledVersion);
                Assert.Equal(vm.Details!.Author, sources.Last().Author ?? string.Empty);
                Assert.True(File.Exists(path));
                Assert.False(vm.IsWaitingForBrowser || vm.IsBusy);
            }
            var cancelled = vm.DownloadAndImportCommand.ExecuteAsync(null);
            await WaitForAyakaBrowserAsync(() => vm.IsWaitingForBrowser);
            await vm.StopAsync(); await cancelled;
            Assert.False(vm.IsWaitingForBrowser || vm.IsBusy);
            Assert.Equal(2, imports.Count);
            using var next = await downloads.StartAsync(vm.SelectedFile!, default);
            Assert.DoesNotContain(ayaka.Paths, p => p.Contains("/download"));
        }
        finally { await vm.CloseAsync(); window.Content = null; window.Close(); }
    }

    private static async Task WaitForAyakaBrowserAsync(Func<bool> waiting, Func<string>? diagnostic = null)
    {
        for (var i = 0; i < 200 && !waiting(); i++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(waiting(), diagnostic?.Invoke());
    }

    [AvaloniaFact]
    public async Task AyakaOAuth_ShouldConfigurePublicClientLoginLogoutAndCancelWhenLeavingSettings()
    {
        var oauth = new AyakaUiOAuthClient();
        await using var scope = await UiScope.CreateAsync(ayakaOAuthOptions: new(), ayakaOAuthClient: oauth);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var account = main.SettingsViewModel.AyakaAccount!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        DevToolsWindow? dev = null;
        try
        {
            window.Show(); main.NavigateToSettingsCommand.Execute(null);
            main.SettingsViewModel.SelectedCategory = "账号";
            void Layout(Window owner)
            { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); using var frame = owner.CaptureRenderedFrame(); }
            void Click(Window owner, Button button)
            {
                Layout(owner); button.BringIntoView(); Layout(owner);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), owner)!.Value;
                Assert.True(button.IsEffectivelyEnabled);
                Assert.Same(button, (owner.InputHitTest(point) as Control)?.FindAncestorOfType<Button>(includeSelf: true));
                owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Layout(owner);
            }
            Layout(window); main.NotificationPanelViewModel.DismissAllToasts(); Layout(window);
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Click(window, settings.FindControl<Button>("SettingsAyakaLoginButton")!);
            await account.Completion;
            Assert.Equal(0, oauth.Logins);
            Assert.False(scope.Services.GetRequiredService<AyakaModsOAuthService>().IsSignedIn);
            dev = new DevToolsWindow { DataContext = new DevToolsViewModel(scope.Services.GetRequiredService<TestDataService>(),
                scope.Services.GetRequiredService<ProfileService>(), scope.Services.GetRequiredService<NexusModsTestService>()) { AyakaAccount = account } };
            dev.Show(); Layout(dev);
            var input = dev.FindControl<TextBox>("AyakaOAuthClientIdInput")!;
            input.BringIntoView(); Layout(dev); input.Focus();
            input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "registered-ui-ayaka" });
            Assert.Equal(AyakaModsOAuthOptions.DefaultRedirectUri, dev.FindControl<TextBox>("AyakaOAuthRedirectUri")!.Text);
            Click(dev, dev.FindControl<Button>("ApplyAyakaOAuthClientIdButton")!);
            await account.ApplyClientIdCommand.ExecutionTask!;
            Assert.Equal("registered-ui-ayaka", scope.Services.GetRequiredService<AyakaModsOAuthService>().ClientId);
            dev.Close();
            Click(window, settings.FindControl<Button>("SettingsAyakaLoginButton")!);
            await account.Completion; Layout(window);
            Assert.True(scope.Services.GetRequiredService<AyakaModsOAuthService>().IsSignedIn);
            Assert.False(scope.Services.GetRequiredService<AyakaModsAuthService>().IsAuthenticated);
            Assert.NotNull(await scope.Services.GetRequiredService<IPlatformCredentialStore>().ReadApiKeyAsync("ayakamods-oauth"));
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                var localization = scope.Services.GetRequiredService<ILocalizationService>();
                await localization.SetLanguageAsync(language); Layout(window);
                Assert.Equal(localization["Ayaka.OAuth.SignedIn"], settings.FindControl<TextBlock>("SettingsAyakaOAuthStatus")!.Text);
            }
            Click(window, settings.FindControl<Button>("SettingsAyakaLogoutButton")!);
            await account.Completion;
            Assert.False(scope.Services.GetRequiredService<AyakaModsOAuthService>().IsSignedIn);
            oauth.WaitForCancel = true;
            Click(window, settings.FindControl<Button>("SettingsAyakaLoginButton")!);
            Assert.True(account.IsLoggingIn);
            main.NavigateToLibraryCommand.Execute(null);
            await account.Completion;
            Assert.True(oauth.Cancelled);
            Assert.False(account.IsLoggingIn);
            Assert.Null(await scope.Services.GetRequiredService<IPlatformCredentialStore>().ReadApiKeyAsync("ayakamods-oauth"));
        }
        finally { dev?.Close(); await account.StopAsync(); window.Close(); }
    }

    private sealed class AyakaUiOAuthClient : IAyakaModsOAuthClient
    {
        public int Logins { get; private set; }
        public bool WaitForCancel { get; set; }
        public bool Cancelled { get; private set; }
        public async Task<AyakaModsOAuthTokens> LoginAsync(AyakaModsOAuthOptions options, Action<Uri> open, CancellationToken token = default)
        {
            options.Validate(); Logins++;
            open(new("https://ayakamods.com/oauth2/authorize"));
            if (WaitForCancel)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new() { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }
        public Task<AyakaModsOAuthTokens> RefreshAsync(AyakaModsOAuthOptions options, AyakaModsOAuthTokens old, CancellationToken token = default)
            => throw new InvalidOperationException("Startup/UI must not refresh tokens.");
    }

    [AvaloniaFact]
    public async Task Ayaka_ShouldSaveKeyInDeveloperToolsBrowseGame119PageAndKeepNexusSeparate()
    {
        using var ayaka = new AyakaUiHandler();
        using var nexus = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: nexus, ayakaHandler: ayaka);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var account = main.SettingsViewModel.AyakaAccount!;
        var dev = new DevToolsWindow
        {
            DataContext = new DevToolsViewModel(scope.Services.GetRequiredService<TestDataService>(),
                scope.Services.GetRequiredService<ProfileService>(), scope.Services.GetRequiredService<NexusModsTestService>())
            { AyakaAccount = account }
        };
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            void Layout(Window owner)
            { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); using var frame = owner.CaptureRenderedFrame(); }
            void Click(Window owner, Button button)
            {
                Layout(owner); button.BringIntoView(); Layout(owner);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), owner)!.Value;
                Assert.Same(button, (owner.InputHitTest(point) as Control)?.FindAncestorOfType<Button>(includeSelf: true));
                owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Layout(owner);
            }
            dev.Show(); Layout(dev);
            var key = dev.FindControl<TextBox>("AyakaApiKeyInput")!;
            key.BringIntoView(); Layout(dev); key.Focus();
            key.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "synthetic-ui-ayaka-key" });
            Click(dev, dev.FindControl<Button>("ApplyAyakaApiKeyButton")!);
            await account.ApplyApiKeyCommand.ExecutionTask!;
            Assert.Empty(key.Text!);
            Assert.True(scope.Services.GetRequiredService<AyakaModsAuthService>().IsAuthenticated);
            Assert.False(scope.Services.GetRequiredService<NexusModsTestService>().IsAuthenticated);
            Assert.Equal("synthetic-ui-ayaka-key", await scope.Services.GetRequiredService<IPlatformCredentialStore>().ReadApiKeyAsync("ayakamods"));
            Assert.Empty(ayaka.Paths);
            dev.Close();
            window.Show(); main.NotificationPanelViewModel.DismissAllToasts();
            await main.NavigateToBrowseCommand.ExecuteAsync(null); Layout(window);
            var vm = main.BrowseModsViewModel!;
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            var selector = view.FindControl<ComboBox>("BrowsePlatformSelector")!;
            selector.Focus(); window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.PlatformChange; Layout(window);
            Assert.True(vm.IsAyaka);
            Assert.Empty(vm.Mods);
            Click(window, view.FindControl<Button>("BrowseRefreshButton")!);
            await vm.RefreshCommand.ExecutionTask!; Layout(window);
            Assert.Equal("/api/mod-games/119/mods/?order=download_count&direction=desc&page=1", ayaka.Paths.Last());
            Assert.Equal("A 网模组", Assert.Single(vm.Mods).Info.Name);
            Assert.False(view.FindControl<Button>("BrowseSearchButton")!.IsEffectivelyEnabled);
            Assert.True(vm.CanNextPage);
            Click(window, view.FindControl<Button>("BrowseNextPageButton")!);
            await vm.NextPageCommand.ExecutionTask!; Layout(window);
            Assert.EndsWith("page=2", ayaka.Paths.Last());
            Assert.True(vm.CanPreviousPage);
            Assert.False(vm.CanNextPage);
            await vm.OpenDetailsCommand.ExecuteAsync(vm.Mods[0]); Layout(window);
            Assert.Equal("42", vm.Details!.Id);
            Assert.Equal("ayaka-file:9:10", vm.SelectedFile!.Id);
            Assert.True(vm.CanDownloadAndImport);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                app.RequestedThemeVariant = theme;
                await scope.Services.GetRequiredService<ILocalizationService>().SetLanguageAsync(language);
                window.Width = 960; window.Height = 600; Layout(window);
                var website = view.FindControl<Button>("BrowseOpenWebsiteButton")!;
                var selectorCenter = selector.TranslatePoint(new Point(0, selector.Bounds.Height / 2), window)!.Value;
                var websiteCenter = website.TranslatePoint(new Point(0, website.Bounds.Height / 2), window)!.Value;
                Assert.InRange(Math.Abs(selectorCenter.Y - websiteCenter.Y), 0, 1);
                foreach (var name in new[] { "BrowseDownloadButton", "BrowseDownloadAndImportButton" })
                {
                    var button = view.FindControl<Button>(name)!;
                    button.BringIntoView(); Layout(window);
                    Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
                    var edge = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                    Assert.InRange(edge.X, 0, window.Bounds.Width); Assert.InRange(edge.Y, 0, window.Bounds.Height);
                }
            }
            vm.SelectedPlatform = vm.Platforms[0]; await vm.PlatformChange; Layout(window);
            Assert.False(vm.HasDetails);
            Assert.Null(vm.SelectedFile);
            Assert.Empty(vm.Mods);
            Assert.True(vm.IsNexus);
        }
        finally
        {
            dev.Close(); await main.BrowseModsViewModel!.CloseAsync();
            window.Close(); app.RequestedThemeVariant = originalTheme;
        }
    }

    private sealed class AyakaUiHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public long FileSize { get; set; } = 4;
        public bool FailDownload { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("synthetic-ui-ayaka-key", request.Headers.GetValues("XF-Api-Key").Single());
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            if (path.Contains("/download?"))
            {
                if (FailDownload) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("""{"errors":[{"code":"unexpected_error_occurred","message":"Synthetic failure"}]}""", Encoding.UTF8, "application/json") });
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("test"));
                content.Headers.ContentType = new("application/zip");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            var body = path.Contains("/mods/?")
                ? """{"mods":[{"mod_id":42,"game_id":119,"title":"A 网模组"}],"pagination":{"current_page":PAGE,"per_page":1,"last_page":2,"total":2}}""".Replace("PAGE", path.EndsWith("page=2") ? "2" : "1")
                : path.EndsWith("/versions/")
                    ? """{"versions":[{"mod_version_id":9,"mod_id":42,"version_string":"2.0","files":[{"id":10,"filename":"armor.zip","size":SIZE}]}]}""".Replace("SIZE", FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : """{"mod":{"mod_id":42,"game_id":119,"title":"A 网模组","description":"模组说明","version_string":"2.0"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
