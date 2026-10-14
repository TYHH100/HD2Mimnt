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
    public async Task Settings_Accounts_ShouldLoginShowLiveQuotaKeepAyakaPlaceholderAndCancelOnLeaving()
    {
        using var handler = new BrowseContractHandler { IncludeQuota = true };
        var oauth = new TestOAuthClient();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler,
            nexusOAuthOptions: new() { ClientId = "registered-settings-client" }, nexusOAuthClient: oauth);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var settings = main.SettingsViewModel;
        var account = settings.NexusAccount!;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show(); main.NavigateToSettingsCommand.Execute(null);
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var categories = view.FindControl<ListBox>("SettingsCategoryList")!;
            var item = categories.GetVisualDescendants().OfType<ListBoxItem>().Single(i => Equals(i.DataContext, "账号"));
            var target = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            window.MouseDown(target, MouseButton.Left); window.MouseUp(target, MouseButton.Left); Layout();
            Assert.True(settings.IsAccountCategoryVisible);
            Assert.Empty(handler.Paths); // 打开账号页不消耗 API 请求。
            void Click(string name)
            {
                var button = view.FindControl<Button>(name)!;
                Layout(); button.BringIntoView(); Layout();
                Assert.True(button.IsEnabled);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                Assert.Same(button, (window.InputHitTest(point) as Control)?.FindAncestorOfType<Button>(includeSelf: true));
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Assert.Contains("未获取", view.FindControl<TextBlock>("NexusHourlyQuota")!.Text);
            var testingHint = view.FindControl<TextBlock>("NexusAccountTestingHint")!;
            Assert.True(testingHint.IsEffectivelyVisible);
            Assert.Contains("工具 → 开发工具", testingHint.Text);
            Assert.Contains("保存并应用 Client ID", testingHint.Text);
            Assert.False(view.FindControl<Button>("SettingsNexusRefreshQuotaButton")!.IsEffectivelyEnabled);
            Click("SettingsNexusLoginButton"); await account.Completion; Layout();
            Assert.True(account.IsSignedIn);
            Assert.False(testingHint.IsEffectivelyVisible);
            Assert.True(main.BrowseModsViewModel!.Account!.IsSignedIn);
            Click("SettingsNexusRefreshQuotaButton"); await account.RefreshRateLimitCommand.ExecutionTask!; Layout();
            Assert.Contains("96 / 100", view.FindControl<TextBlock>("NexusHourlyQuota")!.Text);
            Assert.Contains("2488 / 2500", view.FindControl<TextBlock>("NexusDailyQuota")!.Text);
            Assert.Single(handler.Paths);
            Assert.Equal("/v1/users/validate.json", handler.Paths[0]);
            var ayaka = view.FindControl<FluentAvalonia.UI.Controls.FASettingsExpander>("AyakaAccountSettings")!;
            Assert.True(ayaka.IsEffectivelyVisible);
            Assert.False(ayaka.GetVisualDescendants().OfType<Button>().Last().IsEnabled);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                app.RequestedThemeVariant = variant; await localization.SetLanguageAsync(language);
                window.Width = 960; window.Height = 600; Layout();
                Assert.Contains("96 / 100", view.FindControl<TextBlock>("NexusHourlyQuota")!.Text);
                Assert.Equal(localization["Nexus.Login"], view.FindControl<Button>("SettingsNexusLoginButton")!.Content);
                Assert.True(view.FindControl<Button>("SaveSettingsButton")!.IsEffectivelyVisible == false);
            }
            handler.DelayQuota = true;
            Click("SettingsNexusRefreshQuotaButton");
            settings.SelectedCategory = "游戏";
            await account.StopAsync();
            Assert.True(handler.QuotaCancelled);
            settings.SelectedCategory = "账号";
            handler.DelayQuota = false;
            handler.MembershipStatus = System.Net.HttpStatusCode.TooManyRequests;
            Click("SettingsNexusRefreshQuotaButton"); await account.RefreshRateLimitCommand.ExecutionTask!; Layout();
            Assert.Contains("0 / 100", view.FindControl<TextBlock>("NexusHourlyQuota")!.Text);
            Assert.Contains("0 / 2500", view.FindControl<TextBlock>("NexusDailyQuota")!.Text);
            Assert.Contains("429", ((HD2Mimnt.UI.Services.ModPlatformErrorMessage)account.RateLimitMessage).Localize());
            Assert.True(account.IsSignedIn);
            Assert.NotNull(scope.Services.GetRequiredService<NexusModsAuthService>().RateLimit.RetryAt);
            Click("SettingsNexusLogoutButton"); await account.Completion; Layout();
            Assert.False(main.BrowseModsViewModel.Account.IsSignedIn);
            Assert.Null(scope.Services.GetRequiredService<NexusModsAuthService>().RateLimit.DailyRemaining);
            oauth.WaitForCancel = true;
            Click("SettingsNexusLoginButton"); await oauth.Started.Task;
            main.NavigateToLibraryCommand.Execute(null);
            await account.Completion;
            Assert.False(account.IsBusy);
            Assert.False(account.IsSignedIn);
        }
        finally { await settings.StopAccountsAsync(); window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task DevTools_ClientId_ShouldSaveThroughButtonUpdateLoginAndKeepAfterReopening()
    {
        using var handler = new BrowseContractHandler();
        var oauth = new TestOAuthClient();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler,
            nexusOAuthOptions: new(), nexusOAuthClient: oauth);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await main.NavigateToBrowseCommand.ExecuteAsync(null);
        var account = main.BrowseModsViewModel!.Account!;
        Assert.Contains("申请中", account.Status);
        DevToolsWindow? opened = null;
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<DevToolsWindow>((sender, _) => opened = sender);
        main.OpenDevToolsCommand.Execute(null);
        var window = Assert.IsType<DevToolsWindow>(opened);
        try
        {
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            async Task Apply()
            {
                var button = window.FindControl<Button>("ApplyNexusOAuthClientIdButton")!;
                Layout(); button.BringIntoView(); Layout();
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                Assert.InRange(point.X, 0, window.ClientSize.Width);
                Assert.InRange(point.Y, 0, window.ClientSize.Height);
                Assert.Same(button, (window.InputHitTest(point) as Control)?.FindAncestorOfType<Button>(includeSelf: true));
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                await ((DevToolsViewModel)window.DataContext!).ApplyNexusOAuthClientIdCommand.ExecutionTask!;
                Layout();
            }
            var input = window.FindControl<TextBox>("NexusOAuthClientIdInput")!;
            Assert.Equal('\0', input.PasswordChar); // Client ID 为公开配置，可直接读取核对。
            Assert.Equal(NexusModsOAuthOptions.DefaultRedirectUri, window.FindControl<TextBox>("NexusOAuthCallbackUri")!.Text);
            input.Text = "  registered-ui-client  ";
            await Apply();
            var auth = scope.Services.GetRequiredService<NexusModsAuthService>();
            Assert.Equal("registered-ui-client", auth.OAuthClientId);
            Assert.Equal("registered-ui-client", await scope.Services.GetRequiredService<SettingsService>()
                .GetSettingAsync(SettingKeys.NexusOAuthTestClientId, ""));
            Assert.Equal("尚未登录 N 网。", account.Status);
            await account.LoginCommand.ExecuteAsync(null);
            Assert.Equal("registered-ui-client", oauth.LastClientId);
            input.Text = "another-client";
            await Apply();
            Assert.True(auth.IsOAuthSignedIn);
            Assert.Equal("registered-ui-client", auth.OAuthClientId);
            Assert.Contains("退出", ((DevToolsViewModel)window.DataContext!).NexusOAuthConfigurationMessage);
            await account.LogoutCommand.ExecuteAsync(null);
            window.Close(); main.OpenDevToolsCommand.Execute(null);
            window = Assert.IsType<DevToolsWindow>(opened);
            input = window.FindControl<TextBox>("NexusOAuthClientIdInput")!;
            Assert.Equal("registered-ui-client", input.Text);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            var app = Avalonia.Application.Current!;
            var theme = app.RequestedThemeVariant;
            try
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
                {
                    app.RequestedThemeVariant = variant;
                    await localization.SetLanguageAsync(language);
                    window.Width = 480; window.Height = 400;
                    input.Text = "registered-ui-client";
                    await Apply();
                    Assert.Equal(localization["Nexus.ApplyClientId"], window.FindControl<Button>("ApplyNexusOAuthClientIdButton")!.Content);
                }
            }
            finally { app.RequestedThemeVariant = theme; }
            input.Text = "";
            await Apply();
            Assert.False(auth.IsOAuthConfigured);
            Assert.False(auth.IsAuthenticated);
        }
        finally { window.Close(); await main.BrowseModsViewModel!.StopAsync(); }
    }

    [AvaloniaFact]
    public async Task BrowseAccount_ShouldOnlyShowStatusAndFollowSettingsLoginAndLogout()
    {
        using var handler = new BrowseContractHandler();
        var oauth = new TestOAuthClient();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler,
            nexusOAuthOptions: new() { ClientId = "hd2mimnt-test" }, nexusOAuthClient: oauth);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var auth = scope.Services.GetRequiredService<NexusModsAuthService>();
        Assert.Same(auth, scope.Services.GetRequiredService<NexusModsTestService>());
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            var account = main.BrowseModsViewModel!.Account!;
            var settingsAccount = main.SettingsViewModel.NexusAccount!;
            Assert.Null(view.FindControl<Button>("NexusLoginButton"));
            Assert.Null(view.FindControl<Button>("NexusLogoutButton"));
            Assert.Null(view.FindControl<Button>("NexusManageAuthorizationButton"));
            Assert.Null(view.FindControl<Button>("NexusCancelLoginButton"));
            Assert.Contains("尚未登录", view.FindControl<TextBlock>("NexusAccountStatus")!.Text);
            await settingsAccount.LoginCommand.ExecuteAsync(null);
            Layout();
            Assert.True(auth.IsOAuthSignedIn);
            Assert.True(account.IsSignedIn);
            Assert.Contains("Tester", view.FindControl<TextBlock>("NexusAccountStatus")!.Text);
            Assert.Single(scope.WebsiteLauncher.Urls);
            Assert.StartsWith("https://users.nexusmods.com/oauth/authorize", scope.WebsiteLauncher.Urls[0]);
            Assert.DoesNotContain("synthetic-refresh", System.Text.Json.JsonSerializer.Serialize(
                await scope.Services.GetRequiredService<SettingsService>().GetAllSettingsAsync()));
            await scope.Services.GetRequiredService<HD2Mimnt.Infrastructure.Platforms.NexusModsPlatformAdapter>().GetBrowseDetailsAsync("123");
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                app.RequestedThemeVariant = theme;
                await localization.SetLanguageAsync(language);
                window.Width = 960; window.Height = 600;
                Layout();
                var status = view.FindControl<TextBlock>("NexusAccountStatus")!;
                Assert.True(status.Bounds.Width > 0);
                var point = status.TranslatePoint(new Point(status.Bounds.Width / 2, status.Bounds.Height / 2), window)!.Value;
                Assert.InRange(point.X, 0, window.ClientSize.Width);
                Assert.InRange(point.Y, 0, window.ClientSize.Height);
                Assert.Contains("Tester", view.FindControl<TextBlock>("NexusAccountStatus")!.Text);
                Assert.Equal(2, main.BrowseModsViewModel.Mods.Count);
            }
            settingsAccount.ManageAuthorizationCommand.Execute(null);
            Assert.Equal("https://users.nexusmods.com/oauth/authorized_applications", scope.WebsiteLauncher.Urls.Last());
            await settingsAccount.LogoutCommand.ExecuteAsync(null);
            Layout();
            Assert.False(account.IsSignedIn);
            Assert.Equal(localization["Nexus.SignedOut"], view.FindControl<TextBlock>("NexusAccountStatus")!.Text);
            Assert.False(auth.IsAuthenticated);
            Assert.Null(await scope.Services.GetRequiredService<IPlatformCredentialStore>().ReadApiKeyAsync("nexusmods"));
        }
        finally
        {
            await main.BrowseModsViewModel!.StopAsync();
            window.Close(); app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task NexusOAuth_PendingRegistration_ShouldShowReasonWithoutOpeningBrowserAndKeepKeyTesting()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler, nexusOAuthOptions: new());
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        try
        {
            window.Show();
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            main.NotificationPanelViewModel.DismissAllToasts();
            var account = main.BrowseModsViewModel!.Account!;
            Assert.Contains("申请中", account.Status);
            var settingsAccount = main.SettingsViewModel.NexusAccount!;
            await settingsAccount.LoginCommand.ExecuteAsync(null);
            Assert.Contains("Client ID", settingsAccount.Message);
            Assert.Empty(scope.WebsiteLauncher.Urls);
            await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("synthetic-test-key");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("个人 API Key", account.Status);
            Assert.True(account.HasAuthentication);
            await settingsAccount.LogoutCommand.ExecuteAsync(null);
            Assert.False(account.HasAuthentication);
        }
        finally { window.Close(); }
    }

    private sealed class TestOAuthClient : INexusModsOAuthClient
    {
        public bool WaitForCancel { get; set; }
        public string? LastClientId { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NexusModsOAuthTokens> LoginAsync(NexusModsOAuthOptions options, Action<Uri> openBrowser,
            CancellationToken cancellationToken = default)
        {
            LastClientId = options.ClientId;
            openBrowser(new Uri("https://users.nexusmods.com/oauth/authorize?client_id=" + Uri.EscapeDataString(options.ClientId)));
            if (WaitForCancel)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new() { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30), UserName = "Tester", UserId = "123" };
        }
        public Task<NexusModsOAuthTokens> RefreshAsync(NexusModsOAuthOptions options, NexusModsOAuthTokens tokens,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Refresh not expected");
    }
}
