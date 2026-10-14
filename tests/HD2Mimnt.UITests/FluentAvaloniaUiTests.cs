using System.Reflection;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Resources;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task AddProfileModSearch_ShouldFilterAddAndRestoreRemainingChoices()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var emptyId = await profiles.CreateProfileAsync("搜索添加测试");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Id == emptyId);
        await vm.RetryProfileModsCommand.ExecuteAsync(null);
        await vm.OpenAddModCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var drawer = view.FindControl<Border>("AddProfileModDrawer")!;
            var search = view.FindControl<TextBox>("AddProfileModSearchBox")!;
            var list = view.FindControl<ListBox>("AvailableProfileModList")!;
            var clear = view.FindControl<Button>("ClearAddProfileModSearchButton")!;
            Assert.Equal(3, vm.VisibleAvailableLibraryMods.Count);
            search.Focus();
            search.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = " fluent 2 " });
            Layout();
            Assert.Equal(" fluent 2 ", vm.AddModSearchText);
            var choice = Assert.Single(vm.VisibleAvailableLibraryMods);
            Assert.EndsWith("2", choice.Name);
            Assert.Same(vm.VisibleAvailableLibraryMods, list.ItemsSource);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Layout();
                var searchBottom = search.TranslatePoint(new Point(0, search.Bounds.Height), drawer)!.Value.Y;
                Assert.True(search.IsEffectivelyVisible && search.Bounds.Width > 200);
                Assert.True(searchBottom < list.TranslatePoint(default, drawer)!.Value.Y);
                Capture(window, theme.ToString(), "add-profile-mod-search");
            }
            var add = list.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter == choice);
            void Click(Button button)
            {
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            Click(add);
            await vm.AddModToProfileCommand.ExecutionTask!;
            Layout();
            Assert.True(vm.HasNoAddModSearchResults);
            Assert.False(vm.HasNoAvailableMods);
            Assert.True(view.FindControl<TextBlock>("NoAddModSearchResultsText")!.IsEffectivelyVisible);
            Assert.Equal(choice.Id, Assert.Single((await profiles.GetProfileByIdAsync(emptyId))!.Mods).ModId);
            Click(clear);
            Assert.Equal(string.Empty, search.Text);
            Assert.Equal(2, vm.VisibleAvailableLibraryMods.Count);
            Assert.DoesNotContain(vm.VisibleAvailableLibraryMods, mod => mod.Id == choice.Id);
            search.Text = "helldivers";
            Layout();
            Assert.Equal(2, vm.VisibleAvailableLibraryMods.Count);
            search.Text = "1.0.0";
            Layout();
            Assert.Equal(2, vm.VisibleAvailableLibraryMods.Count);
            search.Text = "不存在的模组";
            Layout();
            Assert.True(vm.HasNoAddModSearchResults);
            vm.CloseAddModCommand.Execute(null);
            await vm.OpenAddModCommand.ExecuteAsync(null);
            Assert.Equal(string.Empty, vm.AddModSearchText);
            Assert.Equal(2, vm.VisibleAvailableLibraryMods.Count);
            foreach (var remaining in vm.VisibleAvailableLibraryMods.ToArray())
                await vm.AddModToProfileCommand.ExecuteAsync(remaining);
            Assert.True(vm.HasNoAvailableMods);
            Assert.False(vm.HasNoAddModSearchResults);
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileSeparators_ShouldAddCollapseExpandAndDropGroupsThroughActualView()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileId = (await profiles.GetActiveProfileAsync())!.Id;
        await profiles.AddSeparatorAsync(profileId, "武器", 0);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var vm = main.ProfileViewModel;
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            Click(view.FindControl<Button>("AddProfileSeparatorButton")!);
            Assert.True(vm.IsSeparatorDialogOpen);
            view.FindControl<TextBox>("SeparatorNameInput")!.Text = "护甲";
            Click(view.FindControl<CheckBox>("UseThemeSeparatorColor")!);
            var picker = view.FindControl<ColorPicker>("SeparatorColorPicker")!;
            Assert.True(picker.IsEffectivelyEnabled);
            Assert.True(picker.Bounds.Width > 0);
            picker.Color = Color.Parse("#E07020");
            Click(view.FindControl<Button>("SaveProfileSeparatorButton")!);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (vm.ConfirmSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
            Assert.False(vm.IsSeparatorDialogOpen);
            Assert.Equal(2, vm.ProfileSeparators.Count);
            var armor = vm.ProfileSeparators.Single(row => row.ModName == "护甲");
            Assert.Equal("#E07020", armor.SeparatorColor);
            Assert.Equal("#E07020", (await profiles.GetProfileByIdAsync(profileId))!.Separators.Single(row => row.Id == armor.ModId).Color);
            var last = vm.ProfileMods.Last();
            await vm.MoveModsAsync(new[] { last.ModId }, Guid.Empty);
            var weapons = vm.ProfileSeparators.Single(row => row.ModName == "武器");
            Assert.Equal(2, weapons.GroupModCount);
            Layout();
            Button Toggle(ProfileModViewModel group) => view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("group-toggle") && button.DataContext == group);
            Assert.Equal(90, Assert.IsType<RotateTransform>(Toggle(weapons).GetVisualDescendants()
                .OfType<HD2Mimnt.UI.Controls.UtilityIcon>().Single(icon => icon.RenderTransform is RotateTransform).RenderTransform).Angle);
            Click(Toggle(weapons));
            while (vm.ToggleSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
            Layout();
            Assert.True(weapons.IsCollapsed);
            Assert.Single(vm.VisibleProfileMods);
            Assert.Equal(last.ModId, vm.VisibleProfileMods[0].ModId);
            Click(Toggle(weapons));
            while (vm.ToggleSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
            Layout();
            Assert.False(weapons.IsCollapsed);
            Assert.Equal(3, vm.VisibleProfileMods.Count);

            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            var firstContainer = (Control)list.ContainerFromIndex(0)!;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(armor.ModId.ToString()));
            list.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, list,
                new Point(5, firstContainer.TranslatePoint(default, list)!.Value.Y), KeyModifiers.None));
            // Drop 启动异步保存，等持久化的分隔符位置反映这次移动。
            while ((await profiles.GetProfileByIdAsync(profileId))!.Separators.First().Id != armor.ModId)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(new[] { armor.ModId, weapons.ModId }, vm.ProfileSeparators.Select(row => row.ModId));
            Assert.Equal(last.ModId, vm.ProfileMods[0].ModId);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task LuaSeparator_ShouldEditColorAndCollapseThroughActualViewInBothDirections()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var profileId = await profiles.CreateProfileAsync("固定分组颜色");
        await profiles.SetActiveProfileAsync(profileId);
        var ordinary = (await repository.GetAllAsync()).First();
        await profiles.AddModToProfileAsync(profileId, ordinary.Id);
        foreach (var role in new[] { HD2Mimnt.Core.Enums.BslModRole.Dependency,
                     HD2Mimnt.Core.Enums.BslModRole.Loader, HD2Mimnt.Core.Enums.BslModRole.SmoothBoot })
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(), Name = $"Color {role}", Version = "1.0",
                Path = Path.Combine(scope.Root, Guid.NewGuid().ToString()), BslRole = role,
                BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion
            };
            await repository.AddAsync(mod);
            await profiles.AddModToProfileAsync(profileId, mod.Id);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var vm = main.ProfileViewModel;
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 1000 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            void Click(Control control)
            {
                Layout();
                var root = TopLevel.GetTopLevel(control)!;
                root.UpdateLayout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
                root.MouseMove(point);
                root.MouseDown(point, MouseButton.Left);
                root.MouseUp(point, MouseButton.Left);
                Layout();
            }
            foreach (var bottomToTop in new[] { false, true })
            {
                await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, bottomToTop ? "BottomToTop" : "TopToBottom");
                await vm.LoadProfilesCommand.ExecuteAsync(null);
                Layout();
                var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
                var group = vm.ProfileRows.Single(row => row.IsSeparator);
                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("profile-separator") && border.DataContext == group);
                Click(card.GetVisualDescendants().OfType<Button>().Single(button => !button.Classes.Contains("group-toggle")));
                var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
                Click(Assert.IsType<FAMenuFlyoutItem>(Assert.Single(menu.Items)));
                Assert.True(vm.IsSeparatorDialogOpen);
                Assert.True(view.FindControl<TextBox>("SeparatorNameInput")!.IsReadOnly);
                if (vm.UseDefaultSeparatorColor) Click(view.FindControl<CheckBox>("UseThemeSeparatorColor")!);
                view.FindControl<ColorPicker>("SeparatorColorPicker")!.Color = Color.Parse("#A050D0");
                Click(view.FindControl<Button>("SaveProfileSeparatorButton")!);
                while (vm.ConfirmSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
                Assert.False(vm.IsSeparatorDialogOpen);
                Assert.Equal("#A050D0", group.SeparatorColor);
                Layout();
                card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("profile-separator") && border.DataContext == group);
                var marker = card.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Background is SolidColorBrush brush && brush.Color == Color.Parse("#A050D0"));
                Assert.True(marker.IsEffectivelyVisible);
                Assert.True(marker.Bounds.Height > 0);
                var before = (await profiles.GetProfileByIdAsync(profileId))!.Mods
                    .Select(mod => (mod.ModId, mod.Enabled, mod.Order)).ToArray();
                var toggle = card.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("group-toggle"));
                var expandedChevron = toggle.GetVisualDescendants().OfType<HD2Mimnt.UI.Controls.UtilityIcon>()
                    .Single(icon => icon.RenderTransform is RotateTransform);
                Assert.True(expandedChevron.IsVisible);
                Assert.Equal(bottomToTop ? -90 : 90, Assert.IsType<RotateTransform>(expandedChevron.RenderTransform).Angle);
                Click(toggle);
                while (vm.ToggleSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
                Assert.True(group.IsCollapsed);
                Assert.Equal(ordinary.Id, Assert.Single(vm.VisibleProfileMods).ModId);
                Assert.Equal(before, (await profiles.GetProfileByIdAsync(profileId))!.Mods.Select(mod => (mod.ModId, mod.Enabled, mod.Order)));
                await vm.LoadProfilesCommand.ExecuteAsync(null);
                Layout();
                group = vm.ProfileRows.Single(row => row.IsSeparator);
                Assert.True(group.IsCollapsed);
                Assert.Equal("#A050D0", group.SeparatorColor);
                Click(view.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("group-toggle") && button.DataContext == group));
                while (vm.ToggleSeparatorCommand.IsRunning) await Task.Delay(10, timeout.Token);
                Assert.False(group.IsCollapsed);
                Assert.Equal(4, vm.VisibleProfileMods.Count);
                // 不重载页面，切换方向后绑定立即更新；收起图标仍是常规右箭头。
                await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, bottomToTop ? "TopToBottom" : "BottomToTop");
                Layout();
                var updatedToggle = view.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains("group-toggle") && button.DataContext == group);
                Assert.Equal(bottomToTop ? 90 : -90, Assert.IsType<RotateTransform>(updatedToggle.GetVisualDescendants()
                    .OfType<HD2Mimnt.UI.Controls.UtilityIcon>().Single(icon => icon.RenderTransform is RotateTransform).RenderTransform).Angle);
            }
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task FreeAccountUpdate_ShouldRequireWebsiteAuthorizationAndReplaceOriginalWithoutRepackedApi()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        var target = library.FilteredMods[0];
        var link = new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633");
        var original = await scope.Services.GetRequiredService<ModLinkService>().SaveAsync(target.Id, link);
        target.ApplyPlatformLink(original);
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            await library.OpenModOnlineCommand.ExecuteAsync(target);
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var online = library.ModOnline!;
            Assert.True(online.UseWebsiteAuthorization);
            Assert.Equal("456", online.SelectedFile!.WebsiteFileId);
            Assert.Contains("自动打开", online.Message);
            Assert.Null(view.FindControl<TextBox>("NxmDownloadLinkInput"));
            view.FindControl<ComboBox>("OnlineFileSelector")!.SelectedItem = online.Files[1];
            Layout();
            var update = view.FindControl<Button>("UpdateOnlineModButton")!;
            Assert.True(update.IsEffectivelyEnabled);
            var p = update.TranslatePoint(new Point(20, 12), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Assert.True(online.IsWaitingForWebsite);
            Assert.False(online.CanDownload);
            Assert.Equal("https://www.nexusmods.com/helldivers2/mods/16633?tab=files&file_id=457&nmm=1",
                scope.WebsiteLauncher.Urls.Single());
            const string authorization = "nxm://helldivers2/mods/16633/files/457?key=test-ui-free-key&expires=4102444800&user_id=789";
            main.ReceiveBrowserProtocolLink(authorization.Replace("/mods/16633/", "/mods/999/"));
            Assert.Contains("另一个模组", online.Message);
            Assert.True(online.IsWaitingForWebsite);
            main.ReceiveBrowserProtocolLink(authorization.Replace("/files/457", "/files/456"));
            Assert.Contains("另一个文件", online.Message);
            main.ReceiveBrowserProtocolLink(authorization.Replace("4102444800", "1"));
            Assert.Contains("已过期", online.Message);
            Assert.Empty(scope.Services.GetRequiredService<DownloadManagerService>().GetAllDownloads());
            // 浏览器启动第二个进程时，经真实管道转发到持有密钥和待处理动作的原窗口。
            var instanceScope = "ui-protocol-" + Guid.NewGuid();
            using var primary = new BrowserProtocolHost(instanceScope);
            using var secondary = new BrowserProtocolHost(instanceScope);
            primary.SetReceiver(value => Dispatcher.UIThread.Post(() => main.ReceiveBrowserProtocolLink(value)));
            Assert.True(await secondary.ForwardAsync(BrowserProtocolHost.GetProtocolArgument(new[] { authorization })));
            Layout();
            await online.UpdateCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Layout();
            Assert.Equal(1, main.SelectedTabIndex);
            Assert.False(online.TryReceiveProtocolLink(authorization));
            Assert.Single(scope.Services.GetRequiredService<DownloadManagerService>().GetAllDownloads());
            Assert.Contains("已替换", online.Message);
            Assert.Equal("3.0", library.FilteredMods.Single(mod => mod.Id == target.Id).Version);
            Assert.False(Directory.Exists(original.Path));
            var saved = (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!;
            Assert.Equal("457", saved.PlatformLink!.InstalledWebsiteFileId);
            Assert.Null(saved.PlatformLink.InstalledFileVersionId);
            Assert.DoesNotContain("test-ui-free-key", System.Text.Json.JsonSerializer.Serialize(saved));
            Assert.DoesNotContain("test-ui-free-key", System.Text.Json.JsonSerializer.Serialize(scope.Services.GetRequiredService<DownloadManagerService>().GetAllDownloads()));
            Assert.DoesNotContain(handler.Requests, path => path.Contains("/v3/") || path.Contains("download-repacked"));
            await online.CloseCommand.ExecuteAsync(null);
            Assert.False(online.IsWaitingForWebsite);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WebsiteAuthorization_ShouldCancelIgnoreLateCallbackAndContinueArchiveDownloadOnly()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        var target = library.FilteredMods[0];
        var link = new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633");
        target.ApplyPlatformLink(await scope.Services.GetRequiredService<ModLinkService>().SaveAsync(target.Id, link));
        const string authorization = "nxm://helldivers2/mods/16633/files/457?key=test-ui-free-key&expires=4102444800&user_id=789";
        var online = library.ModOnline!;
        // 冷启动/无待处理动作时只提示，不修改库或创建下载。
        main.ReceiveBrowserProtocolLink(authorization);
        Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(), n => n.Message.Contains("没有等待"));
        await online.OpenAsync(target);
        online.SelectedFile = online.Files[1];
        var update = online.UpdateCommand.ExecuteAsync(null);
        Assert.True(online.IsWaitingForWebsite);
        online.CancelCommand.Execute(null);
        Assert.False(online.TryReceiveProtocolLink(authorization));
        await update;
        Assert.False(online.IsBusy);
        Assert.Equal("1.0.0", (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!.Version);
        Assert.Empty(scope.Services.GetRequiredService<DownloadManagerService>().GetAllDownloads());
        var path = Path.Combine(scope.Root, "download.zip");
        var wrongAccount = online.DownloadAsync(path);
        Assert.True(online.TryReceiveProtocolLink(authorization.Replace("user_id=789", "user_id=790")));
        await wrongAccount;
        Assert.Contains("账户不同", online.Message);
        Assert.False(File.Exists(path));
        scope.WebsiteLauncher.FailNext = true;
        await online.DownloadAsync(path);
        Assert.Contains("浏览器无法打开", online.Message);
        Assert.False(online.IsWaitingForWebsite);
        var staleSelection = online.DownloadAsync(path);
        var requestedFile = online.SelectedFile;
        online.SelectedFile = online.Files[0];
        Assert.False(online.TryReceiveProtocolLink(authorization));
        await staleSelection;
        Assert.False(File.Exists(path));
        online.SelectedFile = requestedFile;
        var download = online.DownloadAsync(path);
        Assert.True(online.TryReceiveProtocolLink(authorization));
        Assert.False(online.TryReceiveProtocolLink(authorization));
        await download;
        using (var archive = ZipFile.OpenRead(path))
            Assert.NotNull(archive.GetEntry("0123456789abcdef.patch_0"));
        Assert.Equal("1.0.0", (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!.Version);
        var waiting = online.UpdateCommand.ExecuteAsync(null);
        await online.CloseCommand.ExecuteAsync(null);
        await waiting;
        Assert.False(online.IsOpen);
        Assert.False(online.TryReceiveProtocolLink(authorization));
    }

    [AvaloniaFact]
    public async Task ModPlatformErrors_ShouldShowStatusFailedStepAndServerReason()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
        var library = scope.Services.GetRequiredService<LibraryViewModel>();
        library.ModOnline!.UseWebsiteAuthorization = false;
        await library.InitializeAsync();
        var target = library.FilteredMods[0];
        var link = new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633");
        target.ApplyPlatformLink(await scope.Services.GetRequiredService<ModLinkService>().SaveAsync(target.Id, link));
        var window = new Window { Content = new LibraryView { DataContext = library }, Width = 1000, Height = 800 };
        try
        {
            window.Show();
            handler.FailurePath = "/v3/games/helldivers2/mods/16633";
            handler.FailureStatus = System.Net.HttpStatusCode.Unauthorized;
            handler.FailureReason = "API key has been revoked.";
            library.ModLink!.Open(target);
            await library.ModLink.FetchCommand.ExecuteAsync(null);
            Assert.Contains("获取模组信息失败（HTTP 401）", library.ModLink.Message);
            Assert.Contains("更多 → 开发工具", library.ModLink.Message);
            Assert.Contains(handler.FailureReason, library.ModLink.Message);
            library.ModLink.CloseCommand.Execute(null);

            handler.FailurePath = "/v3/mods/nexus_global_16633/files";
            handler.FailureStatus = System.Net.HttpStatusCode.Forbidden;
            handler.FailureReason = "This mod is not accessible to this account.";
            await library.OpenModOnlineCommand.ExecuteAsync(target);
            var online = library.ModOnline!;
            Assert.Contains("获取模组文件列表失败（HTTP 403）", online.Message);
            Assert.Contains(handler.FailureReason, online.Message);
            Assert.DoesNotContain("下载权限", online.Message);

            handler.FailurePath = null;
            await online.CheckCommand.ExecuteAsync(null);
            online.SelectedFile = online.Files[1];
            handler.FailurePath = "/v3/mod-file-versions/version-optional/download-repacked";
            handler.FailureReason = "Premium membership is required for this download.";
            await online.UpdateCommand.ExecuteAsync(null);
            Assert.Contains("获取下载链接失败（HTTP 403）", online.Message);
            Assert.Contains("网页授权下载", online.Message);
            Assert.Contains(handler.FailureReason, online.Message);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == online.Message);

            handler.FailureReason = null;
            await online.UpdateCommand.ExecuteAsync(null);
            Assert.Contains("服务器未提供具体原因", online.Message);
            Assert.Equal("1.0.0", (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!.Version);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryOnlineUpdate_ShouldUseRightClickAndReplaceOriginalThroughActualButton()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        library.ModOnline!.UseWebsiteAuthorization = false;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var target = library.FilteredMods[0];
        var linkService = scope.Services.GetRequiredService<ModLinkService>();
        var link = await linkService.FetchAsync("https://www.nexusmods.com/helldivers2/mods/16633");
        var saved = await linkService.SaveAsync(target.Id, link);
        target.ApplyPlatformLink(saved);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileBefore = (await profiles.GetActiveProfileAsync())!;
        var before = profileBefore.Mods.Single(m => m.ModId == target.Id);
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var card = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("mod-card") && ReferenceEquals(b.DataContext, target));
            var title = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == target.Name);
            var point = title.TranslatePoint(new Point(15, 8), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Layout();
            var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            var entry = menu.Items.OfType<FAMenuFlyoutItem>().Single(i => i.Text == "检查和更新模组…");
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(entry));
            popup.UpdateLayout();
            point = entry.TranslatePoint(new Point(20, 12), popup)!.Value;
            popup.MouseDown(point, MouseButton.Left);
            popup.MouseUp(point, MouseButton.Left);
            await library.OpenModOnlineCommand.ExecutionTask!;
            Layout();
            var online = library.ModOnline!;
            Assert.True(view.FindControl<Border>("ModOnlineDialogSurface")!.IsEffectivelyVisible);
            Assert.Equal("version-1", online.SelectedFile!.Id);
            Assert.Equal(2, online.Files.Count);
            // 通过真实 ComboBox 绑定选择可选文件，验证不会下载默认主版本。
            view.FindControl<ComboBox>("OnlineFileSelector")!.SelectedItem = online.Files[1];
            Assert.Equal("version-optional", online.SelectedFile!.Id);
            var button = view.FindControl<Button>("UpdateOnlineModButton")!;
            Assert.True(button.IsEffectivelyEnabled);
            point = button.TranslatePoint(new Point(20, 12), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await online.UpdateCommand.ExecutionTask!;
            Layout();
            Assert.Contains("已替换", online.Message);
            Assert.Contains("/v3/mod-file-versions/version-optional/download-repacked", handler.Requests);
            Assert.DoesNotContain("/v3/mod-file-versions/version-1/download-repacked", handler.Requests);
            Assert.False(Directory.Exists(saved.Path));
            var updated = library.FilteredMods.Single(m => m.Id == target.Id);
            Assert.Equal(target.Name, updated.Name);
            Assert.Equal("3.0", updated.Version);
            var persisted = (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!;
            Assert.Equal("version-optional", persisted.PlatformLink!.InstalledFileVersionId);
            Assert.Equal("ui downloaded patch", await File.ReadAllTextAsync(Path.Combine(persisted.Path, "0123456789abcdef.patch_0")));
            var after = (await profiles.GetActiveProfileAsync())!.Mods.Single(m => m.ModId == target.Id);
            Assert.Equal(before.Enabled, after.Enabled);
            Assert.Equal(before.Order, after.Order);
            // 关闭期间取消检查，等待执行退出后关闭，晚到响应不能覆盖状态。
            handler.DelayNext = true;
            var check = online.CheckCommand.ExecuteAsync(null);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(view.FindControl<Button>("UpdateOnlineModButton")!.IsEffectivelyEnabled);
            main.CloseOverlaysCommand.Execute(null);
            handler.Release.TrySetResult();
            await check;
            await online.CloseCommand.ExecutionTask!;
            Layout();
            Assert.False(online.IsOpen);
            Assert.False(online.IsBusy);
        }
        finally { handler.Release.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryModLink_ShouldOpenFromRightClickFetchSaveAndRejectStaleResults()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var target = library.FilteredMods[0];
        var other = library.FilteredMods[1];
        other.IsSelected = true;
        var window = new MainWindow { DataContext = main };
        var originalTheme = Avalonia.Application.Current!.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var card = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("mod-card") && ReferenceEquals(b.DataContext, target));
            var title = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == target.Name);
            var point = title.TranslatePoint(new Point(15, 8), window)!.Value;
            var menuAssignments = 0;
            var menuOpened = 0;
            var menuClosed = 0;
            card.PropertyChanged += (_, change) =>
            {
                if (change.Property != Control.ContextFlyoutProperty || card.ContextFlyout is not FAMenuFlyout flyout) return;
                menuAssignments++;
                flyout.Opened += (_, _) => menuOpened++;
                flyout.Closed += (_, _) => menuClosed++;
            };
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            Assert.Null(card.ContextFlyout);
            window.MouseUp(point, MouseButton.Right);
            Layout();
            var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            Assert.Equal(1, menuAssignments);
            Assert.Equal(1, menuOpened);
            Assert.Equal(0, menuClosed);
            var entry = menu.Items.OfType<FAMenuFlyoutItem>().Single(i => i.Text == "模组链接…");
            Assert.True(entry.IsEffectivelyVisible);
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(entry));
            popup.UpdateLayout();
            var menuPoint = entry.TranslatePoint(new Point(20, 12), popup)!.Value;
            popup.MouseDown(menuPoint, MouseButton.Left);
            popup.MouseUp(menuPoint, MouseButton.Left);
            Layout();
            var dialog = library.ModLink!;
            Assert.True(dialog.IsOpen);
            var surface = view.FindControl<Border>("ModLinkDialogSurface")!;
            Assert.True(surface.IsEffectivelyVisible);
            Assert.True(other.IsSelected);
            var input = view.FindControl<TextBox>("ModLinkUrlInput")!;
            void Click(string name)
            {
                Layout();
                var button = view.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyEnabled);
                var p = button.TranslatePoint(new Point(20, 12), window)!.Value;
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
                Layout();
            }
            input.Text = "https://www.nexusmods.com/helldivers2/mods/16633?tab=files";
            Assert.Contains("16633", dialog.Message);
            Assert.False(view.FindControl<Button>("SaveModLinkButton")!.IsEffectivelyEnabled);
            Click("FetchModLinkButton");
            await dialog.FetchCommand.ExecutionTask!;
            Assert.Contains("API 密钥", dialog.Message);
            Assert.Empty(handler.Requests);
            await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
            Click("FetchModLinkButton");
            await dialog.FetchCommand.ExecutionTask!;
            Layout();
            Assert.Equal("API mod title", dialog.Preview!.Name);
            Assert.Equal("2.0", dialog.Preview.LatestVersion);
            Assert.Contains("nexus_global_16633", dialog.Preview.GlobalModId);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                Layout();
                Assert.Equal(byte.MaxValue, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color.A);
                Assert.True(view.FindControl<Button>("SaveModLinkButton")!.IsEffectivelyVisible);
            }
            Click("SaveModLinkButton");
            await dialog.SaveCommand.ExecutionTask!;
            Assert.False(dialog.IsOpen);
            Assert.True(target.HasPlatformLink);
            var saved = (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!;
            Assert.Equal("16633", saved.PlatformModId);
            Assert.Equal("nexusmods", saved.PlatformId);
            Assert.Equal("https://www.nexusmods.com/helldivers2/mods/16633", saved.PlatformLink!.Url);
            Assert.Null((await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(other.Id))!.PlatformLink);
            Assert.Equal(target.Name, saved.Name);
            Assert.Equal("1.0.0", saved.Version);
            library.OpenModLinkCommand.Execute(target);
            Assert.Equal(saved.PlatformLink, dialog.Preview);
            handler.DelayNext = true;
            var fetch = dialog.FetchCommand.ExecuteAsync(null);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Text = "https://www.nexusmods.com/helldivers2/mods/9999";
            Assert.Null(dialog.Preview);
            handler.Release.TrySetResult();
            await fetch;
            Assert.Null(dialog.Preview);
            Assert.Contains("9999", dialog.Message);
            Assert.False(dialog.SaveCommand.CanExecute(null));
            Assert.Equal(saved.PlatformLink, (await scope.Services.GetRequiredService<ModService>().GetModByIdAsync(target.Id))!.PlatformLink);
            main.CloseOverlaysCommand.Execute(null);
            Assert.False(dialog.IsOpen);
            Assert.True(other.IsSelected);
            // 子控件吞掉右键事件时，列表仍须提供模组链接入口。
            var image = card.GetVisualDescendants().OfType<Image>().Single();
            image.AddHandler(InputElement.PointerPressedEvent, (_, args) => args.Handled = true);
            point = image.TranslatePoint(new Point(15, 15), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Layout();
            menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            Assert.True(menu.IsOpen);
            Assert.Contains(menu.Items.OfType<FAMenuFlyoutItem>(), i => i.Text == "模组链接…");
            menu.Hide();
        }
        finally { handler.Release.TrySetResult(); window.Close(); Avalonia.Application.Current!.RequestedThemeVariant = originalTheme; }
    }

    private sealed class ModLinkApiHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public bool DelayNext { get; set; }
        public string? FailurePath { get; set; }
        public System.Net.HttpStatusCode FailureStatus { get; set; }
        public string? FailureReason { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "cdn.example.com")
            {
                Assert.False(request.Headers.Contains("apikey"));
                Assert.Null(request.Headers.Authorization);
                using var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("0123456789abcdef.patch_0").Open()))
                    writer.Write("ui downloaded patch");
                return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
            }
            Assert.Equal("ui-mod-link-key", request.Headers.GetValues("apikey").Single());
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == FailurePath)
                return new(FailureStatus)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { detail = FailureReason }),
                        System.Text.Encoding.UTF8, "application/problem+json")
                };
            if (DelayNext)
            {
                DelayNext = false;
                Started.TrySetResult();
                await Release.Task;
            }
            var json = path switch
            {
                "/v1/games/helldivers2/mods/16633/files.json" => """{"files":[{"file_id":456,"category_id":1,"name":"Main","version":"2.0","uploaded_timestamp":1},{"file_id":457,"category_id":3,"name":"Optional","version":"3.0","uploaded_timestamp":2}]}""",
                "/v1/users/validate.json" => """{"user_id":789,"is_premium":false}""",
                "/v1/games/helldivers2/mods/16633/files/457.json" => """{"file_id":457,"category_id":3,"name":"Optional","version":"3.0","size_kb":1}""",
                "/v1/games/helldivers2/mods/16633/files/457/download_link.json" => """[{"URI":"https://cdn.example.com/free.zip"}]""",
                "/v3/games/helldivers2/mods/16633" => """{"data":{"id":"nexus_global_16633","game_id":"553850","game_scoped_id":"16633","name":"API mod title"}}""",
                "/v3/mods/nexus_global_16633/files" => """{"data":{"mod_files":[{"id":"file-1","name":"Main","is_active":true,"versions_count":1,"archived_count":0,"removed_count":0}]}}""",
                "/v3/mod-files/file-1/versions" => """{"data":{"versions":[{"id":"version-1","file":{"id":"file-1","name":"Main"},"position":"0","game_scoped_id":"1","name":"Main","version":"2.0","category":"main","uploaded_at":"2026-10-11T00:00:00Z","is_primary":true},{"id":"version-optional","file":{"id":"file-1","name":"Main"},"position":"1","game_scoped_id":"2","name":"Optional","version":"3.0","category":"optional","uploaded_at":"2026-10-11T01:00:00Z"}]}}""",
                "/v3/mod-file-versions/version-optional/download-repacked" => """{"download_url":"https://cdn.example.com/mod.zip"}""",
                _ => throw new InvalidOperationException($"Unexpected API path: {path}")
            };
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [AvaloniaFact]
    public async Task DevTools_ShouldSaveRestoreAndClearEncryptedApiKeyThroughActualButtons()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var auth = scope.Services.GetRequiredService<NexusModsTestService>();
        DevToolsWindow? openedWindow = null;
        using var openedSubscription = Window.WindowOpenedEvent.AddClassHandler<DevToolsWindow>(
            (sender, _) => openedWindow = sender);
        // Open the same window as the development-tools navigation command.
        main.OpenDevToolsCommand.Execute(null);
        var window = Assert.IsType<DevToolsWindow>(openedWindow);
        try
        {
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            void Click(string name)
            {
                Layout();
                var button = window.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.Bounds.Width > 0);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            Layout();
            var input = window.FindControl<TextBox>("NexusApiKeyInput")!;
            Assert.Equal('●', input.PasswordChar);
            input.Text = "  ui-test-key  ";
            Click("ApplyNexusApiKeyButton");
            await ((DevToolsViewModel)window.DataContext!).ApplyNexusApiKeyCommand.ExecutionTask!;
            Assert.True(auth.IsAuthenticated);
            Assert.True(auth.IsApiKeySaved);
            // 模拟新启动的认证状态，通过真实 MainWindow 初始化恢复持久化密钥。
            scope.Services.GetRequiredService<HD2Mimnt.Infrastructure.Platforms.NexusModsPlatformAdapter>().ClearAuthentication();
            Assert.False(auth.IsAuthenticated);
            await main.InitializeAsync();
            Assert.True(auth.IsAuthenticated);
            Assert.Equal(string.Empty, input.Text);
            Assert.DoesNotContain("ui-test-key", ((DevToolsViewModel)window.DataContext!).NexusStatusMessage);
            Assert.DoesNotContain("ui-test-key", System.Text.Json.JsonSerializer.Serialize(
                await scope.Services.GetRequiredService<SettingsService>().GetAllSettingsAsync()));
            window.Close();
            main.OpenDevToolsCommand.Execute(null);
            window = Assert.IsType<DevToolsWindow>(openedWindow);
            Assert.Contains("已配置", ((DevToolsViewModel)window.DataContext!).NexusStatusMessage);
            Click("ClearNexusApiKeyButton");
            await ((DevToolsViewModel)window.DataContext!).ClearNexusApiKeyCommand.ExecutionTask!;
            Assert.False(auth.IsAuthenticated);
            await auth.RestoreApiKeyAsync();
            Assert.False(auth.IsAuthenticated);
            Assert.Null(await scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IPlatformCredentialStore>().ReadApiKeyAsync("nexusmods"));
            Click("ApplyNexusApiKeyButton");
            await ((DevToolsViewModel)window.DataContext!).ApplyNexusApiKeyCommand.ExecutionTask!;
            Assert.False(auth.IsAuthenticated);
            Assert.Contains("请输入", ((DevToolsViewModel)window.DataContext!).NexusStatusMessage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Tutorial_ShouldNavigateHighlightAndBlockActionsThroughActualControls()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        await main.ProfileViewModel.LoadProfilesAsync();
        main.SettingsViewModel.SelectedCategory = "外观";
        main.MoreViewModel.SelectedCategory = "教程";
        main.NavigateToToolsCommand.Execute(null);
        var originalProfile = main.ProfileViewModel.SelectedProfile!.Id;
        var profileService = scope.Services.GetRequiredService<ProfileService>();
        string Snapshot(Profile p) => string.Join("|", p.Mods.Select(m => $"{m.ModId}:{m.Enabled}:{m.Order}:{m.OptionsJson}"));
        var originalMods = Snapshot((await profileService.GetProfileByIdAsync(originalProfile))!);
        var app = Avalonia.Application.Current!;
        var originalOpacity = app.Resources["CardSurfaceOpacity"];
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            void Click(Control control)
            {
                var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
                Layout();
            }
            Layout();
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "开始教程"));
            var guide = window.FindControl<HD2Mimnt.UI.Controls.TutorialOverlay>("TutorialGuide")!;
            var next = guide.FindControl<Button>("NextButton")!;
            var previous = guide.FindControl<Button>("PreviousButton")!;
            var frame = guide.FindControl<Border>("TargetFrame")!;
            var card = guide.FindControl<Border>("ExplanationCard")!;
            Assert.True(main.Tutorial.IsOpen);
            Assert.False(previous.IsEnabled);
            Assert.Equal("游戏", main.SettingsViewModel.SelectedCategory);
            Click(next);
            Assert.Equal("部署", main.SettingsViewModel.SelectedCategory);
            Click(previous);
            Assert.Equal(0, main.Tutorial.CurrentStepIndex);

            foreach (var size in new[] { new Size(1280, 800), new Size(960, 600) })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                app.RequestedThemeVariant = theme;
                app.Resources["CardSurfaceOpacity"] = theme == ThemeVariant.Dark ? 0.05 : 0.6;
                Layout();
                for (var index = 0; index < main.Tutorial.Steps.Count; index++)
                {
                    var step = main.Tutorial.CurrentStep;
                    Assert.Equal(index, main.Tutorial.CurrentStepIndex);
                    Assert.Equal(step.PageIndex, main.SelectedTabIndex);
                    Assert.Same(window.FindControl<FANavigationView>("NavigationView")!.MenuItems.OfType<FANavigationViewItem>()
                            .Single(item => item.Tag?.ToString() == step.PageIndex.ToString()),
                        window.FindControl<FANavigationView>("NavigationView")!.SelectedItem);
                    Assert.True(frame.IsEffectivelyVisible, $"Missing target: {step.TargetKey}");
                    var target = window.GetVisualDescendants().OfType<Control>()
                        .First(c => c.Name == step.TargetKey && c.IsEffectivelyVisible);
                    var p = target.TranslatePoint(default, guide)!.Value;
                    Assert.Equal(p.X - 6, Canvas.GetLeft(frame), 1);
                    Assert.Equal(p.Y - 6, Canvas.GetTop(frame), 1);
                    Assert.Equal(target.Bounds.Width + 12, frame.Bounds.Width, 1);
                    Assert.True(card.Bounds.Width > 300 && card.Bounds.Height > 120);
                    Assert.True(Canvas.GetLeft(card) >= 0 && Canvas.GetLeft(card) + card.Bounds.Width <= guide.Bounds.Width);
                    Assert.True(Canvas.GetTop(card) >= 0 && Canvas.GetTop(card) + card.Bounds.Height <= guide.Bounds.Height);
                    Assert.False(new Rect(Canvas.GetLeft(card), Canvas.GetTop(card), card.Bounds.Width, card.Bounds.Height)
                        .Intersects(new Rect(p, target.Bounds.Size)), $"Card covers target: {step.TargetKey}");
                    Assert.IsType<BlurEffect>(guide.FindControl<Border>("BlurredPage")!.Effect);
                    Assert.IsType<VisualBrush>(guide.FindControl<Border>("BlurredPage")!.Background);
                    Assert.False(window.FindControl<Grid>("ApplicationSurface")!.IsHitTestVisible);
                    var cardBackground = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
                    Assert.Equal(byte.MaxValue, cardBackground.Color.A);
                    Assert.Equal(1, cardBackground.Opacity);
                    if (step.TargetKey is "DeployCurrentProfileButton" or "ImportModButton" or "GamePathInput")
                    {
                        Capture(window, theme.ToString(), $"tutorial-{size.Width}-{index}");
                        var previewPath = Environment.GetEnvironmentVariable("HD2MIMNT_UI_PREVIEW_PATH");
                        if (!string.IsNullOrWhiteSpace(previewPath))
                        {
                            using var rendered = window.CaptureRenderedFrame();
                            rendered?.Save(Path.Combine(previewPath, $"{theme}-tutorial-rendered-{size.Width}-{index}.png"));
                        }
                    }
                    if (step.TargetKey is "ScanProfileConflictsButton" or "DeployCurrentProfileButton" or "CleanupDeploymentButton" or "LaunchHd2Button")
                    {
                        Click(target);
                        Assert.False(main.ConflictsViewModel.IsScanning);
                        Assert.False(main.ConflictsViewModel.HasScanned);
                        Assert.False(main.LibraryViewModel.IsDeploying);
                    }
                    if (index == main.Tutorial.Steps.Count - 1) Assert.Equal("完成", next.Content);
                    Click(next);
                }
                Assert.False(main.Tutorial.IsOpen);
                Assert.False(guide.IsEffectivelyVisible);
                Assert.Null(guide.FindControl<Border>("BlurredPage")!.Background);
                Assert.True(window.FindControl<Grid>("ApplicationSurface")!.IsHitTestVisible);
                Assert.Equal(3, main.SelectedTabIndex);
                Assert.Equal("外观", main.SettingsViewModel.SelectedCategory);
                Assert.Equal("教程", main.MoreViewModel.SelectedCategory);
                main.Tutorial.StartCommand.Execute(null);
                Layout();
            }
            window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, null);
            Assert.Equal(4, main.SelectedTabIndex);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Layout();
            Assert.False(main.Tutorial.IsOpen);
            Assert.Equal(3, main.SelectedTabIndex);
            Assert.Equal(originalProfile, main.ProfileViewModel.SelectedProfile!.Id);
            Assert.Equal(originalMods, Snapshot((await profileService.GetProfileByIdAsync(originalProfile))!));

            // 没有模组卡片时不保留上一步的高亮，说明及下一步仍可用。
            var emptyId = await profileService.CreateProfileAsync("教程空配置");
            await main.ProfileViewModel.LoadProfilesAsync();
            main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(p => p.Id == emptyId);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (main.ProfileViewModel.IsProfileModsLoading) await Task.Delay(10, timeout.Token);
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "开始教程"));
            while (main.Tutorial.CurrentStep.TargetKey != "ProfileTutorialCard") Click(next);
            Assert.False(frame.IsVisible);
            Assert.True(guide.FindControl<TextBlock>("TargetUnavailable")!.IsVisible);
            Click(next);
            Assert.True(frame.IsVisible);
            Click(guide.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "退出"));
            Assert.False(main.Tutorial.IsOpen);
            Assert.Equal(3, main.SelectedTabIndex);
        }
        finally { app.Resources["CardSurfaceOpacity"] = originalOpacity; window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryDeleteCommand_ShouldCleanupAndCompactUsingRegisteredServices()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation());
        var mods = scope.Services.GetRequiredService<ModService>();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var source = Path.Combine(scope.Root, "sources", $"delete{i}");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "aaaaaaaaaaaaaaaa.patch_7"), $"delete{i}");
            ids.Add(await mods.ImportModAsync(source));
        }
        var profileId = await profiles.CreateProfileAsync("删除配置");
        await profiles.SetModsEnabledAsync(profileId, ids, true);
        await profiles.SetActiveProfileAsync(profileId);
        var game = Path.Combine(scope.Root, "game");
        Directory.CreateDirectory(game);
        var deployment = scope.Services.GetRequiredService<DeploymentService>();
        var result = await deployment.DeployProfileAsync(profileId, HD2Mimnt.Core.Enums.DeploymentMethod.Copy,
            HD2Mimnt.Core.Enums.DeploymentOrder.TopToBottom, game);
        Assert.Equal(HD2Mimnt.Core.Enums.DeploymentStatus.Completed, result.Status);
        var library = scope.Services.GetRequiredService<LibraryViewModel>();
        await library.InitializeAsync();
        var deleted = library.FilteredMods.Single(mod => mod.Id == ids[1]);

        await library.DeleteModCommand.ExecuteAsync(deleted);

        Assert.Null(await mods.GetModByIdAsync(ids[1]));
        Assert.DoesNotContain(library.FilteredMods, mod => mod.Id == ids[1]);
        Assert.Empty(await deployment.GetModDeploymentHistoryAsync(ids[1]));
        Assert.Equal("delete0", File.ReadAllText(Path.Combine(game, "data", "aaaaaaaaaaaaaaaa.patch_0")));
        Assert.Equal("delete2", File.ReadAllText(Path.Combine(game, "data", "aaaaaaaaaaaaaaaa.patch_1")));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(game, "data")).Length);
        await deployment.PreDeploymentCleanupAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(game, "data")));
    }

    [AvaloniaFact]
    public async Task DownloadSettingsTab_Should_Save_Through_Controls_And_Configure_Runtime_Downloader()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1180, Height = 820, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var category = view.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.DataContext as string == "下载");
            void Click(Control control)
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.UpdateLayout();
            }
            Click(category);
            Assert.Equal("下载", vm.SelectedCategory);
            Assert.True(vm.IsDownloadCategoryVisible);
            Assert.False(vm.IsGameCategoryVisible);
            var expander = view.GetVisualDescendants().OfType<FASettingsExpander>()
                .Single(item => item.Header as string == "下载");
            Assert.True(expander.IsEffectivelyVisible);
            Assert.True(expander.Bounds.Height > 200);
            view.FindControl<NumericUpDown>("DownloadParallelInput")!.Value = 7;
            view.FindControl<NumericUpDown>("DownloadSegmentInput")!.Value = 12;
            view.FindControl<NumericUpDown>("DownloadRetriesInput")!.Value = 4;
            view.FindControl<NumericUpDown>("DownloadRetryDelayInput")!.Value = 900;
            Click(view.FindControl<ToggleSwitch>("DownloadResumeToggle")!);
            Assert.Equal(7, vm.DownloadParallelSegments);
            Assert.Equal(12, vm.DownloadSegmentSizeMiB);
            Assert.False(vm.DownloadEnableResume);
            var save = view.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "保存设置");
            Click(save);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (vm.SaveCommand.IsRunning) await Task.Delay(10, timeout.Token);
            Assert.Equal("下载设置已保存，下次下载生效", vm.StatusMessage);
            var options = await scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IDownloadOptionsProvider>().GetOptionsAsync();
            Assert.Equal(7, options.MaxParallelSegments);
            Assert.Equal(12 * 1024 * 1024, options.MinimumSegmentSize);
            Assert.Equal(4, options.MaxRetries);
            Assert.Equal(TimeSpan.FromMilliseconds(900), options.RetryDelay);
            Assert.False(options.EnableResume);
            Assert.IsType<HD2Mimnt.Infrastructure.Downloads.HttpDownloadService>(
                scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IHttpDownloadService>());
            var registry = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IPlatformRegistry>();
            Assert.NotNull(registry.GetPlatform("nexusmods"));
            Assert.NotNull(registry.GetPlatform("github"));
            Capture(window, "Dark", "download-settings");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileListRightClick_ShouldDeleteInactiveProfileWithoutSelectingOrActivatingIt()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        await profiles.InitializeAsync();
        var deleteId = await profiles.CreateProfileAsync("右键删除目标");
        var switchId = await profiles.CreateProfileAsync("左键切换目标");
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var initial = vm.SelectedProfile!;
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var list = view.FindControl<ListBox>("ProfileList")!;
            Point RowPoint(Guid id)
            {
                var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(vm.Profiles.IndexOf(vm.Profiles.Single(p => p.Id == id))));
                var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == ((ProfileItemViewModel)row.DataContext!).Name);
                return title.TranslatePoint(new Point(15, 8), window)!.Value;
            }
            var point = RowPoint(deleteId);
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
            Assert.Equal(deleteId, Assert.IsType<ProfileItemViewModel>(hit.GetVisualAncestors().OfType<ListBoxItem>().First().DataContext).Id);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            Assert.Same(initial, vm.SelectedProfile);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(initial, vm.SelectedProfile);
            Assert.Equal(initial.Id, (await profiles.GetActiveProfileAsync())!.Id);
            var menu = Assert.IsType<FAMenuFlyout>(list.ContextFlyout);
            Assert.True(menu.IsOpen);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            var delete = Assert.Single(menu.Items.OfType<FAMenuFlyoutItem>(), item => Equals(item.Command, vm.Profiles.Single(p => p.Id == deleteId).DeleteCommand));
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(delete));
            popup.UpdateLayout();
            var deletePoint = delete.TranslatePoint(new Point(20, 12), popup)!.Value;
            popup.MouseMove(deletePoint);
            popup.MouseDown(deletePoint, MouseButton.Left);
            popup.MouseUp(deletePoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; attempt < 100 && vm.Profiles.Any(p => p.Id == deleteId); attempt++) await Task.Delay(10);
            Assert.DoesNotContain(vm.Profiles, p => p.Id == deleteId);
            Assert.Null(await profiles.GetProfileByIdAsync(deleteId));
            Assert.Contains("右键删除目标", confirmation.Message);
            Assert.Same(initial, vm.SelectedProfile);
            Assert.Equal(initial.Id, (await profiles.GetActiveProfileAsync())!.Id);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame();
            point = RowPoint(switchId);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            for (var attempt = 0; attempt < 100 && !vm.Profiles.Single(p => p.Id == switchId).IsActive; attempt++) await Task.Delay(10);
            Assert.Equal(switchId, vm.SelectedProfile!.Id);
            Assert.Equal(switchId, (await profiles.GetActiveProfileAsync())!.Id);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileRightClick_ShouldOpenMenuAndMoveSelectionThroughDialog()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.ProfileViewModel.LoadProfilesCommand.ExecuteAsync(null);
        main.SelectedTabIndex = 0;
        var vm = main.ProfileViewModel;
        var mods = vm.ProfileMods.ToArray();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            Border Card(ProfileModViewModel mod) => view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
            var card = Card(mods[0]);
            var number = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == mods[0].PositionText);
            var chip = Assert.IsType<Border>(number.GetVisualParent());
            Assert.Contains("chip", chip.Classes);
            var row = Assert.IsType<StackPanel>(chip.GetVisualParent());
            Assert.Same(chip, row.Children[0]);
            Assert.Equal("v1.0.0", Assert.Single(row.Children[1].GetVisualDescendants().OfType<TextBlock>()).Text);

            // 真实鼠标事件右键标题，并实际点击弹出菜单。
            var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == mods[0].ModName);
            var point = title.TranslatePoint(new Point(15, 8), window)!.Value;
            var menuAssignments = 0;
            var menuOpened = 0;
            var menuClosed = 0;
            card.PropertyChanged += (_, change) =>
            {
                if (change.Property != Control.ContextFlyoutProperty || card.ContextFlyout is not FAMenuFlyout flyout) return;
                menuAssignments++;
                flyout.Opened += (_, _) => menuOpened++;
                flyout.Closed += (_, _) => menuClosed++;
            };
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            Assert.Null(card.ContextFlyout);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            Assert.Equal(1, menuAssignments);
            Assert.Equal(1, menuOpened);
            Assert.Equal(0, menuClosed);
            Assert.True(menu.IsOpen);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            var item = Assert.Single(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "移动到指定位置…");
            Assert.Equal("移动到指定位置…", item.Text);
            Assert.True(item.IsEffectivelyVisible);
            var openLocationItem = Assert.Single(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "打开文件位置");
            Assert.True(openLocationItem.IsEffectivelyVisible);
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(item));
            popup.UpdateLayout();
            var itemPoint = item.TranslatePoint(new Point(20, 12), popup)!.Value;
            popup.MouseMove(itemPoint);
            popup.MouseDown(itemPoint, MouseButton.Left);
            popup.MouseUp(itemPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsMoveToPositionDialogOpen);
            Assert.True(view.FindControl<Border>("MoveToPositionDialogSurface")!.IsEffectivelyVisible);
            var input = view.FindControl<Border>("MoveToPositionDialogSurface")!.GetVisualDescendants().OfType<TextBox>().Single();
            input.Text = "3";
            var moveButton = view.FindControl<Border>("MoveToPositionDialogSurface")!.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "移动"));
            window.UpdateLayout();
            var movePoint = moveButton.TranslatePoint(new Point(20, 12), window)!.Value;
            window.MouseMove(movePoint);
            window.MouseDown(movePoint, MouseButton.Left);
            window.MouseUp(movePoint, MouseButton.Left);
            for (var attempt = 0; attempt < 100 && vm.IsMoveToPositionDialogOpen; attempt++) await Task.Delay(20);
            Assert.False(vm.IsMoveToPositionDialogOpen);
            Assert.Equal(new[] { mods[1].ModId, mods[2].ModId, mods[0].ModId }, vm.ProfileMods.Select(mod => mod.ModId));
            var persisted = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(vm.SelectedProfile!.Id);
            Assert.Equal(vm.ProfileMods.Select(mod => mod.ModId), persisted!.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));

            // 多选：右键图片区域，子控件已经处理事件时也应打开菜单并保留选择。
            mods[1].IsSelected = true;
            mods[0].IsSelected = true;
            window.UpdateLayout();
            card = Card(mods[1]);
            var image = card.GetVisualDescendants().OfType<Image>().Single();
            image.AddHandler(InputElement.PointerPressedEvent, (_, args) => args.Handled = true);
            point = image.TranslatePoint(new Point(15, 15), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            Assert.True(menu.IsOpen);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            item = Assert.Single(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "移动到指定位置…");
            popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(item));
            popup.UpdateLayout();
            itemPoint = item.TranslatePoint(new Point(20, 12), popup)!.Value;
            popup.MouseMove(itemPoint);
            popup.MouseDown(itemPoint, MouseButton.Left);
            popup.MouseUp(itemPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsMoveToPositionDialogOpen);
            Assert.Equal(2, vm.ProfileMods.Count(mod => mod.IsSelected));
            input.Text = "1";
            window.UpdateLayout();
            movePoint = moveButton.TranslatePoint(new Point(20, 12), window)!.Value;
            window.MouseMove(movePoint);
            window.MouseDown(movePoint, MouseButton.Left);
            window.MouseUp(movePoint, MouseButton.Left);
            for (var attempt = 0; attempt < 100 && vm.IsMoveToPositionDialogOpen; attempt++) await Task.Delay(20);
            Assert.False(vm.IsMoveToPositionDialogOpen);
            Assert.Equal(new[] { mods[1].ModId, mods[0].ModId, mods[2].ModId }, vm.ProfileMods.Select(mod => mod.ModId));
            persisted = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(vm.SelectedProfile!.Id);
            Assert.Equal(vm.ProfileMods.Select(mod => mod.ModId), persisted!.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileCards_ShouldMarkLuaResourcesAndKeepOrdinaryModsUnmarked()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var stored = (await repository.GetAllAsync()).OrderBy(mod => mod.Name).ToArray();
        stored[0].ResourceTypeIds = new() { 0xa14e8dfa2cd117e2UL };
        stored[1].ResourceTypeIds = new() { 0xcd4238c6a0c69e32UL, 0xa14e8dfa2cd117e2UL };
        stored[2].ResourceTypeIds = new() { 0xcd4238c6a0c69e32UL };
        stored[2].Name = "Lua Mods 名称不能代替类型识别";
        foreach (var mod in stored) await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.ProfileViewModel.LoadProfilesCommand.ExecuteAsync(null);
        main.SelectedTabIndex = 0;
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
                var labels = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.Text == "Lua Mods" && text.IsEffectivelyVisible).ToArray();
                Assert.Equal(2, labels.Length);
                Assert.All(labels, label => Assert.Contains(
                    Assert.IsType<ProfileModViewModel>(label.DataContext).ModId, stored.Take(2).Select(mod => mod.Id)));
                Assert.False(main.ProfileViewModel.ProfileMods.Single(mod => mod.ModId == stored[2].Id).IsLuaMod);
                Capture(window, theme.ToString(), "profile-lua-mods");
            }
            foreach (var role in new[] { HD2Mimnt.Core.Enums.BslModRole.Dependency, HD2Mimnt.Core.Enums.BslModRole.Loader,
                         HD2Mimnt.Core.Enums.BslModRole.MdlLoader, HD2Mimnt.Core.Enums.BslModRole.SmoothBoot })
                Assert.True(new ProfileModViewModel { BslRole = role }.IsLuaMod);
            Assert.False(new ProfileModViewModel { IsSeparator = true }.IsLuaMod);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task MdlAndSmoothBoot_ShouldSwitchExclusiveLoaderAndKeepHighestPriorityInBothDirections()
    {
        await using var scope = await UiScope.CreateAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var mods = scope.Services.GetRequiredService<ModService>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var id = await profiles.CreateProfileAsync("加载器例外");
        await profiles.SetActiveProfileAsync(id);
        var vm = scope.Services.GetRequiredService<ProfileViewModel>();
        var ids = new List<Guid>();
        foreach (var role in new[] { "normal", "dependency", "bsl", "mdl", "sb" })
        {
            var source = Path.Combine(scope.Root, "sources", role);
            Directory.CreateDirectory(source);
            var guid = role switch
            {
                "bsl" => "612eaf70-d682-43c7-9efd-16dcc695f977",
                "mdl" => "761188b2-c45b-4607-9241-f89ca221c47b",
                "sb" => "7c3e9a52-1b84-4f6a-9c2d-8e5f0a1b2c3d",
                _ => System.Guid.NewGuid().ToString()
            };
            File.WriteAllText(Path.Combine(source, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1, Guid = guid, Name = role,
                Description = role is "dependency" or "sb" ? "Requires Bingus Shared Loader v15 or newer / API 1." : ""
            }));
            File.WriteAllText(Path.Combine(source, "9ba626afa44a3aa3.patch_0"), role);
            var modId = await mods.ImportModAsync(source);
            ids.Add(modId);
            await profiles.AddModToProfileAsync(id, modId);
        }
        await vm.LoadProfilesAsync();
        Assert.False(vm.ProfileMods.Single(row => row.ModId == ids[2]).Enabled);
        Assert.True(vm.ProfileMods.Single(row => row.ModId == ids[3]).Enabled);
        Assert.Equal(ids, vm.ProfileMods.Select(row => row.ModId));
        Assert.Equal(ids[4], vm.ProfileMods.Last().ModId);
        Assert.False(vm.ProfileMods.Last().CanDrag);
        await vm.ToggleModEnabledCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == ids[2]));
        Assert.True(vm.ProfileMods.Single(row => row.ModId == ids[2]).Enabled);
        Assert.False(vm.ProfileMods.Single(row => row.ModId == ids[3]).Enabled);
        await vm.ToggleModEnabledCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == ids[3]));
        Assert.False(vm.ProfileMods.Single(row => row.ModId == ids[2]).Enabled);
        Assert.True(vm.ProfileMods.Single(row => row.ModId == ids[3]).Enabled);
        Assert.False(vm.CanMoveMods(new[] { ids[4] }, ids[2]));
        Assert.False(vm.CanMoveMods(new[] { ids[1] }, ids[4]));

        var view = new ProfileView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            var label = ((Control)list.ContainerFromIndex(5)!).GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Text == "SmoothBoot · 最高优先级");
            Assert.True(label.IsEffectivelyVisible);
            await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, "BottomToTop");
            Assert.Equal(new[] { ids[4], ids[3], ids[2], ids[1], ids[0] }, vm.ProfileMods.Select(row => row.ModId));
            Assert.False(vm.CanMoveMods(new[] { ids[4] }, null));
            Assert.False(vm.CanMoveMods(new[] { ids[1] }, ids[3]));
            await vm.RetryProfileModsCommand.ExecuteAsync(null);
            Assert.Equal(ids[4], vm.ProfileMods.First().ModId);
            await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, "TopToBottom");
            Assert.Equal(ids[4], vm.ProfileMods.Last().ModId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BslGroup_ShouldAutoCreateRestrictDropsAndFollowDeploymentDirection()
    {
        await using var scope = await UiScope.CreateAsync();
        var services = scope.Services;
        var profiles = services.GetRequiredService<ProfileService>();
        var mods = services.GetRequiredService<ModService>();
        var settings = services.GetRequiredService<SettingsService>();
        var id = await profiles.CreateProfileAsync("BSL 配置");
        await profiles.SetActiveProfileAsync(id);
        var vm = services.GetRequiredService<ProfileViewModel>();
        await vm.LoadProfilesAsync();
        Assert.True(Assert.Single(vm.ProfileRows).IsSeparator);

        var ids = new List<Guid>();
        foreach (var role in new[] { "normal", "dependency", "loader" })
        {
            var source = Path.Combine(scope.Root, "sources", role);
            Directory.CreateDirectory(source);
            var manifest = new
            {
                Version = 1,
                Guid = role == "loader" ? "612eaf70-d682-43c7-9efd-16dcc695f977" : System.Guid.NewGuid().ToString(),
                Name = role,
                Description = role == "dependency" ? "Requires Bingus Shared Loader v15 or newer / API 1." : ""
            };
            File.WriteAllText(Path.Combine(source, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(source, "9ba626afa44a3aa3.patch_0"), role);
            ids.Add(await mods.ImportModAsync(source));
        }
        await profiles.SetModsEnabledAsync(id, ids.ToArray(), true);
        await vm.RetryProfileModsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { ids[0], Guid.Empty, ids[1], ids[2] }, vm.ProfileRows.Select(row => row.ModId));
        Assert.False(vm.ProfileMods.Last().CanDrag);
        Assert.False(vm.CanMoveMods(new[] { ids[0] }, ids[1]));
        Assert.False(vm.CanMoveMods(new[] { ids[0] }, null));
        Assert.False(vm.CanMoveMods(new[] { ids[1] }, Guid.Empty));
        Assert.False(vm.CanMoveMods(new[] { ids[2] }, ids[0]));
        Assert.True(vm.CanMoveMods(new[] { ids[0] }, Guid.Empty));
        Assert.True(vm.CanMoveMods(new[] { ids[1] }, ids[2]));
        await vm.MoveModsAsync(new[] { ids[0] }, ids[1]);
        Assert.Equal(new[] { ids[0], ids[1], ids[2] }, vm.ProfileMods.Select(row => row.ModId));

        var view = new ProfileView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            Assert.Equal(4, list.ItemCount);
            var target = (Control)list.ContainerFromIndex(2)!;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(ids[0].ToString()));
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(8, 0), KeyModifiers.None);
            target.RaiseEvent(over);
            Assert.Equal(DragDropEffects.None, over.DragEffects);
            var drop = new DragEventArgs(DragDrop.DropEvent, data, target, new Point(8, 0), KeyModifiers.None);
            target.RaiseEvent(drop);
            Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.False(view.FindControl<Border>("SortInsertionMarker")!.IsVisible);

            await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, HD2Mimnt.Core.Enums.DeploymentOrder.BottomToTop.ToString());
            window.UpdateLayout();
            Assert.Equal(new[] { ids[2], ids[1], Guid.Empty, ids[0] }, vm.ProfileRows.Select(row => row.ModId));
            var dependencyRow = (Control)list.ContainerFromIndex(1)!;
            var separatorRow = (Control)list.ContainerFromIndex(2)!;
            var normalRow = (Control)list.ContainerFromIndex(3)!;
            Assert.True(dependencyRow.TranslatePoint(default, list)!.Value.Y < separatorRow.TranslatePoint(default, list)!.Value.Y);
            Assert.True(separatorRow.TranslatePoint(default, list)!.Value.Y < normalRow.TranslatePoint(default, list)!.Value.Y);
            var boundaryOver = new DragEventArgs(DragDrop.DragOverEvent, data, separatorRow, new Point(8, 0), KeyModifiers.None);
            separatorRow.RaiseEvent(boundaryOver);
            Assert.Equal(DragDropEffects.None, boundaryOver.DragEffects);
            Assert.False(vm.CanMoveMods(new[] { ids[0] }, ids[1]));
            Assert.False(vm.CanMoveMods(new[] { ids[1] }, null));
            Assert.False(vm.CanMoveMods(new[] { ids[2] }, ids[0]));
            Assert.True(vm.CanMoveMods(new[] { ids[0] }, null));
            Assert.False(vm.CanMoveMods(new[] { ids[1] }, ids[0]));
            Assert.False(vm.CanMoveMods(new[] { ids[0] }, Guid.Empty));
            Assert.True(vm.CanMoveMods(new[] { ids[1] }, Guid.Empty));
            await vm.RetryProfileModsCommand.ExecuteAsync(null);
            Assert.Equal(ids[2], vm.ProfileMods.First().ModId);
            await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, HD2Mimnt.Core.Enums.DeploymentOrder.TopToBottom.ToString());
            Assert.Equal(ids[2], vm.ProfileMods.Last().ModId);
            vm.ModSearchText = "dependency";
            Assert.Equal(new[] { Guid.Empty, ids[1] }, vm.ProfileRows.Select(row => row.ModId));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileDeploymentButton_ShouldUseSelectedProfileAndSavedMethodAndGameDataDirectory()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation);
        var services = scope.Services;
        var mods = services.GetRequiredService<ModService>();
        var profiles = services.GetRequiredService<ProfileService>();
        var settings = services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var source = Path.Combine(scope.Root, "sources", $"mod{index}");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "0123456789abcdef.patch_9"), $"mod{index}");
            ids.Add(await mods.ImportModAsync(source));
        }
        var activeId = await profiles.CreateProfileAsync("激活配置");
        var selectedId = await profiles.CreateProfileAsync("查看的配置");
        await profiles.AddModToProfileAsync(activeId, ids[0]);
        await profiles.AddModToProfileAsync(selectedId, ids[1]);
        await profiles.AddModToProfileAsync(selectedId, ids[2]);
        await profiles.SetActiveProfileAsync(activeId);
        var game = Path.Combine(scope.Root, "Helldivers 2");
        foreach (var name in new[] { "data", "tools", "bin" }) Directory.CreateDirectory(Path.Combine(game, name));
        File.WriteAllText(Path.Combine(game, "bin", "helldivers2.exe"), string.Empty);

        var main = services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        await main.ProfileViewModel.LoadProfilesAsync();
        main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(profile => profile.Id == selectedId);
        main.SelectedTabIndex = 0;
        var window = new MainWindow { DataContext = main };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var originalOpacity = app.Resources["CardSurfaceOpacity"];
        try
        {
            window.Show();
            var selectedMod = (await mods.GetModsByIdsAsync(new[] { ids[1] })).Single();
            foreach (var method in new[] { HD2Mimnt.Core.Enums.DeploymentMethod.HardLink, HD2Mimnt.Core.Enums.DeploymentMethod.Copy })
            {
                main.SettingsViewModel.GamePath = game;
                main.SettingsViewModel.AutoDetectGame = false;
                main.SettingsViewModel.AutoSwitchProfileOnSelect = false;
                main.SettingsViewModel.DefaultDeploymentMethod = method;
                main.SettingsViewModel.DeployBottomToTop = method == HD2Mimnt.Core.Enums.DeploymentMethod.Copy;
                await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
                Assert.Equal("设置已保存", main.SettingsViewModel.StatusMessage);
                // 模拟未保存的下拉修改，部署必须仍使用已保存值。
                main.SettingsViewModel.DefaultDeploymentMethod = HD2Mimnt.Core.Enums.DeploymentMethod.SymbolicLink;
                window.UpdateLayout();
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(item => item.Content as string == "部署当前配置文件");
                Assert.Equal(selectedId, button.CommandParameter);
                Assert.True(button.IsEffectivelyEnabled);
                Assert.Same(main.LibraryViewModel.DeployCommand, button.Command);
                confirmation.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                for (var attempt = 0; attempt < 100 && !confirmation.PendingConfirmation; attempt++) await Task.Delay(10);
                Assert.True(confirmation.PendingConfirmation);
                var surface = window.GetVisualDescendants().OfType<ProfileView>().Single()
                    .FindControl<Border>("DeploymentOperationSurface")!;
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme;
                    app.Resources["CardSurfaceOpacity"] = 0.05;
                    window.UpdateLayout();
                    Assert.True(surface.IsEffectivelyVisible);
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background);
                    Assert.Equal(1.0, background.Opacity);
                    Assert.Equal(byte.MaxValue, background.Color.A);
                }
                confirmation.Pending.SetResult(true);
                for (var attempt = 0; attempt < 300 && main.LibraryViewModel.DeployCommand.IsRunning; attempt++) await Task.Delay(20);
                Assert.False(main.LibraryViewModel.DeployCommand.IsRunning);
                window.UpdateLayout();
                Assert.False(surface.IsEffectivelyVisible);
                Assert.True(confirmation.Message.Contains("查看的配置"), string.Join("; ", services.GetRequiredService<NotificationService>()
                    .GetRecentNotifications().Select(notification => notification.Message)));
                var deployments = services.GetRequiredService<DeploymentService>();
                var records = (await deployments.GetLatestDeploymentsAsync()).ToList();
                Assert.True(records.Count == 2, string.Join("; ", services.GetRequiredService<NotificationService>()
                    .GetRecentNotifications().Select(notification => notification.Message)));
                Assert.All(records, record =>
                {
                    Assert.Equal(selectedId, record.ProfileId);
                    Assert.Equal(method.ToString(), record.DeploymentMethod);
                    Assert.Equal("Completed", record.Status);
                });
                Assert.Contains("查看的配置", confirmation.Message);
                Assert.Contains("启用模组：2 个", confirmation.Message);
                Assert.Contains(Path.Combine(game, "data"), confirmation.Message);
                var target = Path.Combine(game, "data", "0123456789abcdef.patch_0");
                var sourcePatch = Path.Combine(selectedMod.Path, "0123456789abcdef.patch_9");
                if (method == HD2Mimnt.Core.Enums.DeploymentMethod.Copy)
                    sourcePatch = Path.Combine((await mods.GetModByIdAsync(ids[2]))!.Path, "0123456789abcdef.patch_9");
                var before = File.ReadAllText(target);
                File.AppendAllText(sourcePatch, " changed");
                Assert.Equal(method == HD2Mimnt.Core.Enums.DeploymentMethod.HardLink ? before + " changed" : before, File.ReadAllText(target));
                Assert.Equal(6, Directory.GetFiles(Path.Combine(game, "data")).Length);
                Assert.Empty(Directory.GetFiles(game));
                Assert.Equal(activeId, (await profiles.GetActiveProfileAsync())!.Id);
            }
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            app.RequestedThemeVariant = originalTheme;
            app.Resources["CardSurfaceOpacity"] = originalOpacity;
        }
    }

    private sealed class RecordingConfirmation : IConfirmationService
    {
        public string Message { get; private set; } = string.Empty;
        public TaskCompletionSource<bool>? Pending { get; set; }
        public bool PendingConfirmation => Pending != null && ReferenceEquals(_shown, Pending);
        private TaskCompletionSource<bool>? _shown;
        public Task<bool> ConfirmAsync(string title, string message)
        {
            Message = message;
            _shown = Pending;
            return Pending?.Task ?? Task.FromResult(true);
        }
    }

    [AvaloniaFact]
    public async Task AddToProfileDialog_ShouldStayOpaqueWhenCardOpacityChangesAndFollowTheme()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        main.SelectedTabIndex = 1;
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var originalOpacity = app.Resources["CardSurfaceOpacity"];
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            await main.LibraryViewModel.OpenAddToProfileDialogCommand.ExecuteAsync(main.LibraryViewModel.FilteredMods[0]);
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var surface = view.FindControl<Border>("AddToProfileDialogSurface")!;
            Color? previousColor = null;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                foreach (var opacity in new[] { 0.05, 0.6, 1.0 })
                {
                    app.Resources["CardSurfaceOpacity"] = opacity;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.True(surface.IsEffectivelyVisible);
                    Assert.True(surface.Bounds.Width > 0 && surface.Bounds.Height > 0);
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background);
                    Assert.Equal(1.0, background.Opacity);
                    Assert.Equal(byte.MaxValue, background.Color.A);
                    Assert.Equal((Color)surface.FindResource(surface.ActualThemeVariant, "SolidBackgroundFillColorTertiary")!, background.Color);
                    Assert.Equal(opacity, Brush(window, "CardFillBrush").Opacity);
                }
                var color = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;
                if (previousColor is { } previous) Assert.NotEqual(previous, color);
                previousColor = color;
                Capture(window, theme.ToString(), "add-to-profile");
            }
            main.LibraryViewModel.CancelAddToProfileCommand.Execute(null);
            Assert.False(surface.IsEffectivelyVisible);

            await main.ProfileViewModel.LoadProfilesCommand.ExecuteAsync(null);
            main.SelectedTabIndex = 0;
            var mod = main.ProfileViewModel.ProfileMods[0];
            mod.HasOptions = true;
            main.ProfileViewModel.OpenModOptionsCommand.Execute(mod);
            var optionsSurface = window.GetVisualDescendants().OfType<ProfileView>().Single()
                .FindControl<Border>("ModOptionsDialogSurface")!;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var opacity in new[] { 0.05, 0.6, 1.0 })
            {
                app.RequestedThemeVariant = theme;
                app.Resources["CardSurfaceOpacity"] = opacity;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(optionsSurface.IsEffectivelyVisible);
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(optionsSurface.Background);
                Assert.Equal(1.0, background.Opacity);
                Assert.Equal(byte.MaxValue, background.Color.A);
                Assert.Equal((Color)optionsSurface.FindResource(optionsSurface.ActualThemeVariant, "SolidBackgroundFillColorTertiary")!, background.Color);
                Assert.Equal(opacity, Brush(window, "CardFillBrush").Opacity);
            }
            main.ProfileViewModel.CloseModOptionsCommand.Execute(null);
            Assert.False(optionsSurface.IsEffectivelyVisible);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            app.Resources["CardSurfaceOpacity"] = originalOpacity;
            app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task ModDetails_ShouldCalculateMissingHashPersistItAndShowReadableFailure()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var saved = (await repository.GetAllAsync()).OrderBy(mod => mod.Name).ToArray();
        saved[0].Hash = string.Empty;
        await repository.UpdateAsync(saved[0]);
        saved[1].Hash = string.Empty;
        saved[1].Path = Path.Combine(scope.Root, "missing-mod");
        await repository.UpdateAsync(saved[1]);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        // 列表加载不应主动补算哈希。
        Assert.Empty((await repository.GetByIdAsync(saved[0].Id))!.Hash);
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            var mod = library.FilteredMods.Single(item => item.Id == saved[0].Id);
            library.ShowModDetailCommand.Execute(mod);
            for (var attempt = 0; attempt < 150 && mod.IsCalculatingHash; attempt++) await Task.Delay(20);
            Assert.False(mod.IsCalculatingHash);
            Assert.Matches("^[0-9a-f]{64}$", mod.Hash);
            Assert.Equal(mod.Hash, (await repository.GetByIdAsync(mod.Id))!.Hash);
            window.UpdateLayout();
            var panel = window.GetVisualDescendants().OfType<HD2Mimnt.UI.Controls.ModDetailPanel>().Single();
            Assert.Equal(mod.Hash, panel.FindControl<TextBlock>("ModHashValue")!.Text);
            var broken = library.FilteredMods.Single(item => item.Id == saved[1].Id);
            library.ShowModDetailCommand.Execute(broken);
            for (var attempt = 0; attempt < 150 && broken.IsCalculatingHash; attempt++) await Task.Delay(20);
            Assert.False(broken.IsCalculatingHash);
            Assert.StartsWith("计算失败：", broken.HashDisplay);
            Assert.Empty((await repository.GetByIdAsync(broken.Id))!.Hash);
            library.CloseDetailPanelCommand.Execute(null);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryMultiSelection_ShouldPreserveFocusOnlyClicksAndSupportModifiersKeyboardAndBlankSpace()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        main.SelectedTabIndex = 1;
        var vm = main.LibraryViewModel;
        var mods = vm.FilteredMods.ToArray();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var list = view.FindControl<ItemsControl>("ModList")!;
            var toolbar = view.FindControl<WrapPanel>("LibrarySelectionToolbar")!;
            void AssertSelectionView()
            {
                Assert.Equal(vm.SelectedCount > 0, toolbar.IsVisible);
                foreach (var item in vm.FilteredMods)
                {
                    var container = list.GetVisualDescendants().OfType<ListBoxItem>()
                        .Single(control => ReferenceEquals(control.DataContext, item));
                    Assert.Equal(item.IsHighlighted, container.IsSelected);
                }
            }
            void Click(int index, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.UpdateLayout();
                var card = list.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mods[index]));
                var point = card.TranslatePoint(new Point(100, 15), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
                AssertSelectionView();
            }
            void Keys(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPress(key, modifiers, PhysicalKey.None, null);
                window.KeyRelease(key, modifiers, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
                AssertSelectionView();
            }
            Click(0);
            Assert.Same(mods[0], vm.FocusedMod);
            Assert.Empty(vm.SelectedMods);
            Assert.False(toolbar.IsVisible);
            Assert.True(mods[0].IsHighlighted);
            Click(1);
            Assert.False(mods[0].IsHighlighted);
            Assert.True(mods[1].IsHighlighted);
            Assert.False(toolbar.IsVisible);
            Click(0);
            CaptureSingleClickMarker(window, "library-single-click");
            Click(0, RawInputModifiers.Control);
            Assert.Equal(mods[0], Assert.Single(vm.SelectedMods));
            Assert.True(toolbar.IsVisible);
            Click(0);
            Assert.Empty(vm.SelectedMods);
            Assert.False(toolbar.IsVisible);
            Click(2, RawInputModifiers.Shift);
            Assert.Equal(mods, vm.SelectedMods);
            Click(1, RawInputModifiers.Shift);
            Assert.Equal(mods.Take(2), vm.SelectedMods);
            Click(2, RawInputModifiers.Control);
            Assert.Equal(3, vm.SelectedCount);
            Click(2, RawInputModifiers.Control);
            Assert.Equal(2, vm.SelectedCount);
            Click(1, RawInputModifiers.Shift);
            Assert.Equal(mods.Skip(1), vm.SelectedMods);
            Click(0, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(3, vm.SelectedCount);
            list.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            Keys(Key.A, RawInputModifiers.Control);
            Assert.Equal(3, vm.SelectedCount);
            Keys(Key.Escape);
            Assert.Empty(vm.SelectedMods);
            Assert.All(mods, mod => Assert.False(mod.IsHighlighted));
            vm.FocusMod(mods[0]);
            Keys(Key.Down, RawInputModifiers.Shift);
            Assert.Equal(mods.Take(2), vm.SelectedMods);
            Keys(Key.Down, RawInputModifiers.Shift);
            Assert.Equal(3, vm.SelectedCount);
            Keys(Key.Up, RawInputModifiers.Shift);
            Assert.Equal(mods.Take(2), vm.SelectedMods);
            var search = view.FindControl<TextBox>("SearchBox")!;
            search.Text = "existing text";
            search.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            Assert.Equal("existing text", search.SelectedText);
            Assert.Equal(2, vm.SelectedCount);
            search.Text = string.Empty;
            // 行内按钮只执行自身操作，不清空已有的批量选择。
            var selectedBeforeAction = vm.SelectedMods.ToArray();
            var action = list.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "加入 Profile…"));
            var actionPoint = action.TranslatePoint(new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), window)!.Value;
            window.MouseDown(actionPoint, MouseButton.Left);
            window.MouseUp(actionPoint, MouseButton.Left);
            for (var attempt = 0; attempt < 50 && !vm.IsAddToProfileDialogOpen; attempt++) await Task.Delay(20);
            Assert.True(vm.IsAddToProfileDialogOpen);
            Assert.Equal(selectedBeforeAction, vm.SelectedMods);
            AssertSelectionView();
            vm.CancelAddToProfileCommand.Execute(null);
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(list.Parent);
            var blank = scroll.TranslatePoint(new Point(100, scroll.Bounds.Height - 12), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.Empty(vm.SelectedMods);
            AssertSelectionView();

            // 搜索后的容器重新生成时，选中外观仍应跟随已有的批量选择。
            Click(0, RawInputModifiers.Control);
            Click(2, RawInputModifiers.Control);
            vm.SearchText = mods[0].Name;
            await vm.ExecuteSearchCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(2, vm.SelectedCount);
            AssertSelectionView();
            vm.SearchText = string.Empty;
            await vm.ExecuteSearchCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(2, vm.SelectedCount);
            AssertSelectionView();
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryCards_ShouldMatchProfileSpacingAndKeepLongTextInsideCards()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var longName = "HD2Mimnt Test " + new string('模', 80);
        await scope.Services.GetRequiredService<TestDataService>().ImportGeneratedModsAsync(new[]
        {
            new Mod
            {
                Name = longName, Author = new string('作', 80), Version = "1.0.0",
                Description = new string('描', 120), Tags = new() { "标签一", "标签二" },
                ResourceTypeIds = ResourceTypeCatalog.All.Select(type => type.Id).ToList(),
                Path = Path.Combine(scope.Root, "sources", "long-card")
            }
        });
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.ProfileViewModel.LoadProfilesAsync();
        await main.LibraryViewModel.InitializeAsync();
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var profileCard = window.GetVisualDescendants().OfType<Border>()
                .First(card => card.Classes.Contains("mod-card") && card.IsEffectivelyVisible);
            var profileMod = Assert.IsType<ProfileModViewModel>(profileCard.DataContext);
            var profileName = profileCard.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == profileMod.ModName);
            var nameInset = profileName.TranslatePoint(default, profileCard)!.Value.X;
            var cardHeight = profileCard.Bounds.Height;
            main.SelectedTabIndex = 1;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 1280d, 1000d })
            {
                app.RequestedThemeVariant = theme;
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
                var cards = view.GetVisualDescendants().OfType<Border>()
                    .Where(card => card.Classes.Contains("mod-card")).ToArray();
                Assert.Equal(4, cards.Length);
                foreach (var card in cards)
                {
                    var mod = Assert.IsType<ModItemViewModel>(card.DataContext);
                    var name = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == mod.Name);
                    var thumbnail = card.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("mod-thumbnail"));
                    var action = card.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "加入 Profile…"));
                    var namePosition = name.TranslatePoint(default, card)!.Value;
                    var thumbnailPosition = thumbnail.TranslatePoint(default, card)!.Value;
                    var actionPosition = action.TranslatePoint(default, card)!.Value;
                    Assert.Equal(cardHeight, card.Bounds.Height, 1);
                    Assert.Equal(nameInset, namePosition.X, 1);
                    Assert.True(thumbnailPosition.X > 0 && thumbnailPosition.Y > 0);
                    Assert.True(namePosition.X + name.Bounds.Width <= actionPosition.X);
                    Assert.True(actionPosition.X + action.Bounds.Width < card.Bounds.Width);
                    foreach (var text in card.GetVisualDescendants().OfType<TextBlock>())
                    {
                        var position = text.TranslatePoint(default, card)!.Value;
                        Assert.True(position.X + text.Bounds.Width <= card.Bounds.Width);
                        if (!text.GetVisualAncestors().OfType<Button>().Any())
                            Assert.True(position.X + text.Bounds.Width <= actionPosition.X);
                    }
                }
                Capture(window, theme.ToString(), $"library-cards-{width}");
            }
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task ProfileLuaSelection_ShouldSelectDependenciesAndSkipFixedCards()
    {
        await using var scope = await UiScope.CreateAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var id = await profiles.CreateProfileAsync("Lua 多选");
        await profiles.SetActiveProfileAsync(id);
        foreach (var role in new[] { HD2Mimnt.Core.Enums.BslModRole.None,
                     HD2Mimnt.Core.Enums.BslModRole.Dependency, HD2Mimnt.Core.Enums.BslModRole.Dependency,
                     HD2Mimnt.Core.Enums.BslModRole.Loader, HD2Mimnt.Core.Enums.BslModRole.MdlLoader,
                     HD2Mimnt.Core.Enums.BslModRole.SmoothBoot })
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(), Name = $"Selection {role}", Version = "1.0",
                Path = Path.Combine(scope.Root, Guid.NewGuid().ToString()), BslRole = role,
                BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion
            };
            await repository.AddAsync(mod);
            await profiles.AddModToProfileAsync(id, mod.Id);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var savedOrder = (await profiles.GetProfileByIdAsync(id))!.Mods.Select(mod => mod.ModId).ToArray();
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 1000 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            void Click(ProfileModViewModel mod, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
                Assert.True(card.IsEffectivelyVisible);
                var point = card.TranslatePoint(new Point(100, 15), window)!.Value;
                var hit = window.InputHitTest(point) as Visual;
                Assert.True(hit != null && (ReferenceEquals(hit, card) || hit.GetVisualAncestors().Contains(card)), $"Target: {mod.BslRole}, hit: {hit}, name: {(hit as Control)?.Name}, data: {(hit as Control)?.DataContext}, point: {point}");
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, modifiers);
            }
            void Keys(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPress(key, modifiers, PhysicalKey.None, null);
                window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            }
            foreach (var direction in new[] { "TopToBottom", "BottomToTop" })
            {
                await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, direction);
                vm.ModSearchText = string.Empty;
                window.UpdateLayout();
                list.Focus();
                foreach (var mod in vm.ProfileMods) mod.IsSelected = false;
                var fixedCards = vm.ProfileMods.Where(mod => !mod.CanDrag).ToArray();
                foreach (var mod in fixedCards)
                {
                    Click(mod);
                    Click(mod, RawInputModifiers.Control);
                    Click(mod, RawInputModifiers.Shift);
                }
                Assert.Empty(vm.ProfileMods.Where(mod => mod.IsSelected));

                var lua = vm.VisibleProfileMods.Where(mod => mod.IsLuaDependency).ToArray();
                Click(lua[0]);
                Click(lua[^1], RawInputModifiers.Shift);
                Assert.Equal(lua, vm.ProfileMods.Where(mod => mod.IsSelected));
                Click(lua[1], RawInputModifiers.Shift);
                Assert.Equal(lua.Take(2), vm.ProfileMods.Where(mod => mod.IsSelected));
                Click(lua[^1], RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal(lua, vm.ProfileMods.Where(mod => mod.IsSelected));
                Click(lua[1], RawInputModifiers.Control);
                Assert.False(lua[1].IsSelected);
                Click(lua[1], RawInputModifiers.Control);
                Assert.True(lua[1].IsSelected);
                Click(fixedCards[0], RawInputModifiers.Shift);
                Assert.Equal(lua, vm.ProfileMods.Where(mod => mod.IsSelected));
                Assert.False(Assert.Single(vm.ProfileRows, row => row.IsSeparator).IsSelected);

                var dependency = lua.First(mod => mod.IsLuaDependency);
                Assert.False(vm.CanMoveMods(fixedCards.Select(mod => mod.ModId).ToArray(), Guid.Empty));
                var separator = (Control)list.ContainerFromIndex(vm.ProfileRows.ToList().FindIndex(row => row.IsSeparator))!;
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(string.Join(';', fixedCards.Select(mod => mod.ModId))));
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, separator, new Point(8, 0), KeyModifiers.None);
                separator.RaiseEvent(over);
                Assert.Equal(DragDropEffects.None, over.DragEffects);
                Assert.False(vm.CanMoveMods(new[] { dependency.ModId, vm.ProfileMods.Single(mod => !mod.IsBslMember).ModId }, Guid.Empty));

                Keys(Key.A, RawInputModifiers.Control);
                Keys(Key.A, RawInputModifiers.Control);
                Assert.All(vm.ProfileMods, mod => Assert.Equal(mod.CanSelect, mod.IsSelected));
                foreach (var mod in vm.ProfileMods) mod.IsSelected = false;
                vm.ModSearchText = "SmoothBoot";
                window.UpdateLayout();
                list.Focus();
                Keys(Key.A, RawInputModifiers.Control);
                Assert.Empty(vm.ProfileMods.Where(mod => mod.IsSelected));
                Assert.False(Assert.Single(vm.ProfileRows, row => row.IsSeparator).IsSelected);
            }
            Assert.Equal(savedOrder, (await profiles.GetProfileByIdAsync(id))!.Mods.Select(mod => mod.ModId));
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileMultiSelection_ShouldPreserveFocusOnlyClicksAndSupportModifiersKeyboardAndBlankSpace()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        main.Tutorial.CloseCommand.Execute(null);
        var vm = main.ProfileViewModel;
        var mods = vm.VisibleProfileMods.ToArray();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            var toolbar = view.FindControl<WrapPanel>("ProfileSelectionToolbar")!;
            void AssertSelection()
            {
                Assert.Equal(mods.Count(mod => mod.IsSelected), vm.SelectedModCount);
                Assert.Equal(vm.HasSelectedMods, toolbar.IsVisible);
                foreach (var mod in vm.VisibleProfileMods)
                    Assert.Equal(mod.IsHighlighted, view.GetVisualDescendants().OfType<ListBoxItem>()
                        .Single(item => ReferenceEquals(item.DataContext, mod)).IsSelected);
            }
            void Keys(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPress(key, modifiers, PhysicalKey.None, null);
                window.KeyRelease(key, modifiers, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AssertSelection();
            }
            void ToolbarClick(string label)
            {
                window.UpdateLayout();
                var button = toolbar.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                AssertSelection();
            }
            void Click(int index, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.UpdateLayout();
                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mods[index]));
                var point = card.TranslatePoint(new Point(100, 15), window)!.Value;
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
                AssertSelection();
            }
            Click(1);
            Assert.Same(mods[1], vm.FocusedMod);
            Assert.False(vm.HasSelectedMods);
            Assert.False(toolbar.IsVisible);
            Assert.True(mods[1].IsHighlighted);
            Click(0);
            Assert.False(mods[1].IsHighlighted);
            Assert.True(mods[0].IsHighlighted);
            Assert.False(toolbar.IsVisible);
            Click(1);
            CaptureSingleClickMarker(window, "profile-single-click");
            Click(1, RawInputModifiers.Control);
            Assert.Equal(mods.Skip(1).Take(1), mods.Where(mod => mod.IsSelected));
            Assert.True(toolbar.IsVisible);
            Click(1);
            Assert.False(vm.HasSelectedMods);
            Assert.False(toolbar.IsVisible);
            Click(2, RawInputModifiers.Shift);
            Assert.Equal(mods.Skip(1), mods.Where(mod => mod.IsSelected));
            Click(0, RawInputModifiers.Shift);
            Assert.Equal(mods.Take(2), mods.Where(mod => mod.IsSelected));
            Click(2, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.All(mods, mod => Assert.True(mod.IsSelected));
            // 按住已选卡片时保留整组，供拖动使用；未拖动松开时才只聚焦并清空选择。
            window.UpdateLayout();
            var selectedCard = view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mods[0]));
            var selectedPoint = selectedCard.TranslatePoint(new Point(100, 15), window)!.Value;
            window.MouseDown(selectedPoint, MouseButton.Left);
            Assert.All(mods, mod => Assert.True(mod.IsSelected));
            window.MouseUp(selectedPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            AssertSelection();
            Assert.Same(mods[0], vm.FocusedMod);
            Assert.False(vm.HasSelectedMods);
            Click(2, RawInputModifiers.Control);
            Assert.Equal(mods.Skip(2), mods.Where(mod => mod.IsSelected));
            Assert.True(list.IsFocused, $"Focus: {window.FocusManager?.GetFocusedElement()?.GetType().Name}");
            Assert.False(vm.IsModOptionsDialogOpen || vm.IsMoveToPositionDialogOpen || vm.IsAddModOpen || main.Tutorial.IsOpen,
                $"Options={vm.IsModOptionsDialogOpen}, Move={vm.IsMoveToPositionDialogOpen}, Add={vm.IsAddModOpen}, Tutorial={main.Tutorial.IsOpen}");
            Keys(Key.Escape);
            Assert.False(vm.HasSelectedMods);
            Assert.All(mods, mod => Assert.False(mod.IsHighlighted));
            Keys(Key.A, RawInputModifiers.Control);
            Keys(Key.A, RawInputModifiers.Control);
            Assert.Equal(3, vm.SelectedModCount);
            ToolbarClick("反选");
            Assert.False(vm.HasSelectedMods);
            list.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            ToolbarClick("启用");
            Keys(Key.Escape);
            Assert.False(vm.HasSelectedMods);
            Click(0, RawInputModifiers.Control);
            ToolbarClick("全选 / 取消全选");
            Assert.Equal(3, vm.SelectedModCount);
            ToolbarClick("全选 / 取消全选");
            Assert.False(vm.HasSelectedMods);
            list.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            var scroll = view.FindControl<ScrollViewer>("ModScrollViewer")!;
            var blank = scroll.TranslatePoint(new Point(100, scroll.Bounds.Height - 12), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.False(vm.HasSelectedMods);
            AssertSelection();
            list.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            vm.ModSearchText = mods[0].ModName;
            window.UpdateLayout();
            ToolbarClick("全选 / 取消全选");
            Assert.Equal(2, vm.SelectedModCount);
            ToolbarClick("清空选择");
            Assert.False(vm.HasSelectedMods);
            vm.ModSearchText = string.Empty;
            Click(2, RawInputModifiers.Shift);
            Assert.Equal(mods.Skip(2), mods.Where(mod => mod.IsSelected));
            var nameInput = view.FindControl<StackPanel>("CreateProfilePanel")!.GetVisualDescendants().OfType<TextBox>().Single();
            nameInput.Text = "existing text";
            nameInput.Focus();
            Keys(Key.A, RawInputModifiers.Control);
            Assert.Equal("existing text", nameInput.SelectedText);
            Assert.Equal(1, vm.SelectedModCount);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileSelectionToolbar_ShouldPersistBatchActionsToViewedProfile()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.ProfileViewModel.LoadProfilesAsync();
        var vm = main.ProfileViewModel;
        var profileId = vm.SelectedProfile!.Id;
        var mods = vm.VisibleProfileMods.ToArray();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            async Task ClickAction(string label)
            {
                window.UpdateLayout();
                var button = view.FindControl<WrapPanel>("ProfileSelectionToolbar")!.GetVisualDescendants()
                    .OfType<Button>().Single(button => Equals(button.Content, label));
                var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await (command.ExecutionTask ?? Task.CompletedTask);
                Dispatcher.UIThread.RunJobs();
            }
            mods[0].IsSelected = mods[1].IsSelected = true;
            await ClickAction("禁用");
            var profile = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(profileId);
            Assert.All(profile!.Mods.Where(mod => mods.Take(2).Any(item => item.ModId == mod.ModId)), mod => Assert.False(mod.Enabled));
            Assert.True(profile.Mods.Single(mod => mod.ModId == mods[2].ModId).Enabled);
            await ClickAction("启用");
            profile = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(profileId);
            Assert.All(profile!.Mods, mod => Assert.True(mod.Enabled));
            await ClickAction("移出配置");
            profile = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(profileId);
            Assert.Equal(mods[2].ModId, Assert.Single(profile!.Mods).ModId);
            Assert.Equal(mods[2], Assert.Single(vm.ProfileMods));
            Assert.Equal(1, vm.SelectedProfile.ModCount);
            Assert.False(vm.HasSelectedMods);
            Assert.Equal(3, (await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).Count());
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task AddMod_ShouldOpenFloatingDialogAndReportBatchStepsCountsAndFailures()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main };
        var originalTheme = Avalonia.Application.Current!.RequestedThemeVariant;
        try
        {
            window.Show();
            window.UpdateLayout();
            var addButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Content, "添加模组"));
            addButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(library.Import.IsOpen);
            var overlay = window.FindControl<HD2Mimnt.UI.Controls.ModImportOverlay>("ModImportOverlay")!;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Avalonia.Application.Current.RequestedThemeVariant = theme;
                window.UpdateLayout();
                Assert.True(overlay.IsEffectivelyVisible);
                Assert.InRange(overlay.FindControl<Border>("ImportDialog")!.Bounds.Width, 500, 560);
                Capture(window, theme.ToString(), "mod-import-select");
            }
            var source = Path.Combine(scope.Root, "HD2Mimnt Test Progress.zip");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("body.patch").Open()))
                writer.Write("test content");
            var steps = new HashSet<string>();
            library.Import.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModImportViewModel.Step)) steps.Add(library.Import.Step);
            };
            // 第一项失败后继续导入；重复路径只处理一次。
            var importing = library.ImportPathsAsync(new[] { Path.Combine(scope.Root, "missing.zip"), source, source });
            Assert.True(library.Import.IsImporting);
            Assert.False(library.Import.CloseCommand.CanExecute(null));
            Assert.True(library.Import.CancelCommand.CanExecute(null));
            // 同时发起的新批次不能重置计数或再导入文件。
            await library.ImportPathsAsync(new[] { source });
            await importing;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("解压模组", steps);
            Assert.Contains("复制模组文件", steps);
            Assert.Contains("生成缩略图", steps);
            Assert.Contains("计算哈希", steps);
            Assert.Equal(2, library.Import.TotalCount);
            Assert.Equal(2, library.Import.ProcessedCount);
            Assert.Equal(0, library.Import.RemainingCount);
            Assert.Equal(1, library.Import.SuccessCount);
            Assert.Equal(1, library.Import.FailedCount);
            Assert.Single(library.Import.Errors);
            Assert.Equal(100, library.Import.OverallProgress);
            Assert.Equal("添加结束，部分模组失败", library.Import.Step);
            Assert.True(library.Import.IsOpen);
            Assert.Equal("已处理 2 / 2 · 剩余 0 个", overlay.FindControl<TextBlock>("ImportCounts")!.Text);
            Assert.Single(await scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>().GetAllAsync());
            Assert.Single(library.FilteredMods);
            window.UpdateLayout();
            Capture(window, "Dark", "mod-import-result");
            library.Import.CloseCommand.Execute(null);
            Assert.False(library.Import.IsOpen);
        }
        finally
        {
            Avalonia.Application.Current.RequestedThemeVariant = originalTheme;
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ModImport_Cancel_ShouldRetainCompletedModsAndRemainingCount()
    {
        await using var scope = await UiScope.CreateAsync();
        var library = scope.Services.GetRequiredService<MainWindowViewModel>().LibraryViewModel;
        var sources = new[] { "first", "second" }.Select(name => Path.Combine(scope.Root, name)).ToArray();
        foreach (var source in sources)
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "test content");
        }
        library.Import.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModImportViewModel.SuccessCount) && library.Import.SuccessCount == 1)
                library.Import.CancelCommand.Execute(null);
        };
        await library.ImportPathsAsync(sources);
        Dispatcher.UIThread.RunJobs();
        Assert.False(library.Import.IsImporting);
        Assert.Equal("添加已取消", library.Import.Step);
        Assert.Equal(1, library.Import.SuccessCount);
        Assert.Equal(1, library.Import.ProcessedCount);
        Assert.Equal(1, library.Import.RemainingCount);
        Assert.Equal(50, library.Import.OverallProgress);
        Assert.Empty(library.Import.Errors);
        Assert.Single(library.FilteredMods);
        Assert.Single(await scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>().GetAllAsync());
    }

    [AvaloniaFact]
    public async Task FileImportDrag_ShouldCoverWindowWithoutFlickeringOrBreakingInternalSort()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        var archivePath = Path.Combine(scope.Root, "HD2Mimnt Test Window Drop.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("body.patch").Open()))
            writer.Write("test mod content");
        try
        {
            window.Show();
            window.UpdateLayout();
            var file = await window.StorageProvider.TryGetFileFromPathAsync(archivePath);
            Assert.NotNull(file);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            var hint = window.FindControl<Border>("ImportDragHint")!;
            Assert.False(hint.IsHitTestVisible);
            window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, window, new Point(640, 400), KeyModifiers.None));
            Assert.True(hint.IsVisible);
            var changes = 0;
            EventHandler<AvaloniaPropertyChangedEventArgs> watch = (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty) changes++;
            };
            hint.PropertyChanged += watch;
            try
            {
                for (var tab = 0; tab < 5; tab++)
                {
                    main.SelectedTabIndex = tab;
                    window.UpdateLayout();
                    var targets = window.GetVisualDescendants().OfType<Control>()
                        .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0
                            && control.Bounds.Height > 0 && (control is TextBlock || control is Button || control is Border))
                        .Take(60).ToArray();
                    foreach (var target in targets)
                    {
                        var point = new Point(1, 1);
                        var position = target.TranslatePoint(point, window);
                        if (position is null || !new Rect(window.Bounds.Size).Contains(position.Value)) continue;
                        target.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, target, point, KeyModifiers.None));
                        Assert.True(hint.IsVisible);
                        var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, point, KeyModifiers.None);
                        target.RaiseEvent(over);
                        Assert.Equal(DragDropEffects.Copy, over.DragEffects);
                    }
                }
                Assert.Equal(0, changes);
            }
            finally { hint.PropertyChanged -= watch; }
            window.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, window, new Point(-1, -1), KeyModifiers.None));
            Assert.False(hint.IsVisible);
            var internalData = new DataTransfer();
            internalData.Add(DataTransferItem.CreateText(Guid.NewGuid().ToString()));
            var internalOver = new DragEventArgs(DragDrop.DragOverEvent, internalData, window, new Point(100, 100), KeyModifiers.None)
                { DragEffects = DragDropEffects.Move };
            window.RaiseEvent(internalOver);
            Assert.False(internalOver.Handled);
            Assert.Equal(DragDropEffects.Move, internalOver.DragEffects);
            window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, window, new Point(640, 400), KeyModifiers.None));
            var drop = new DragEventArgs(DragDrop.DropEvent, data, window, new Point(640, 400), KeyModifiers.None);
            window.RaiseEvent(drop);
            Assert.True(drop.Handled);
            Assert.False(hint.IsVisible);
            for (var attempt = 0; attempt < 150 && main.LibraryViewModel.IsLoading; attempt++) await Task.Delay(20);
            Assert.False(main.LibraryViewModel.IsLoading);
            var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
            Assert.Equal("HD2Mimnt Test Window Drop", Assert.Single(await repository.GetAllAsync()).Name);
            window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, window, new Point(640, 400), KeyModifiers.None));
            window.DataContext = null;
            Assert.False(hint.IsVisible);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Notifications_ShouldShowBoundedBannersPreserveHistoryAndStopOnDetach()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var service = scope.Services.GetRequiredService<NotificationService>();
        var vm = main.NotificationPanelViewModel;
        var window = new MainWindow { DataContext = main };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            await Task.Run(() =>
            {
                service.ShowSuccess("模组已加入当前配置文件", "添加完成");
                service.ShowInfo("当前配置文件已保存", "保存完成");
                service.ShowWarning("两个模组使用相同的目标文件，请检查覆盖顺序", "发现冲突");
                service.ShowWithNavigation(NotificationLevel.Error, "未检测到有效游戏目录，请在设置中选择安装位置", "打开设置", NotificationNavigationTarget.Settings, "无法部署");
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.Toasts.Count);
            Assert.Equal(4, service.GetRecentNotifications().Count());
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                window.UpdateLayout();
                await Task.Delay(280);
                Capture(window, theme.ToString(), "notification-banners");
                var cards = window.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("notification-toast")).ToArray();
                Assert.Equal(3, cards.Length);
                Assert.All(cards, card => Assert.True(card.Bounds.Width > 300));
            }
            var actionable = vm.Toasts[0];
            vm.ExecuteActionCommand.Execute(actionable);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, main.SelectedTabIndex);
            Assert.DoesNotContain(actionable, vm.Toasts);
            vm.DismissToastCommand.Execute(vm.Toasts[0]);
            Assert.Equal(3, service.GetRecentNotifications().Count());
            main.OpenNotificationsCommand.Execute(null);
            Assert.Empty(vm.Toasts);
            service.ShowInfo("在通知面板打开时只更新历史", "面板消息");
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Toasts);
            main.CloseNotificationsCommand.Execute(null);
            service.ShowInfo("自动收起仍保留历史", "横幅计时");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(vm.Toasts);
            await Task.Delay(6300);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Toasts);
            Assert.Contains(service.GetRecentNotifications(), item => item.Title == "横幅计时");
            window.Content = null;
            service.ShowInfo("视图已卸载", "生命周期检查");
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Toasts);
        }
        finally
        {
            app.RequestedThemeVariant = originalTheme;
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LibraryCategories_ShouldWorkWithoutStateFilterNavigation()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var viewModel = scope.Services.GetRequiredService<LibraryViewModel>();
        var repository = scope.Services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var stored = (await repository.GetAllAsync()).ToArray();
        stored[0].ResourceTypeIds = new() { 0xcd4238c6a0c69e32UL, 0xe0a48d0be9a7453fUL };
        stored[1].ResourceTypeIds = new() { 0x535a7bd3e650d799UL, ulong.MaxValue };
        stored[2].ResourceTypeIds = new();
        foreach (var mod in stored) await repository.UpdateAsync(mod);
        await viewModel.InitializeAsync();
        var mods = viewModel.FilteredMods.ToArray();
        var view = new LibraryView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var filters = view.GetVisualDescendants().OfType<RadioButton>()
                .Where(button => button.GroupName == "LibraryFilter").ToArray();
            Assert.Empty(filters);
            Assert.Equal(ModFilterType.All, viewModel.CurrentFilter);
            var selectors = view.GetVisualDescendants().OfType<ComboBox>().ToArray();
            var category = Assert.Single(selectors.Where(box => ReferenceEquals(box.ItemsSource, viewModel.CategoryOptions)));
            category.SelectedValue = 0xcd4238c6a0c69e32UL;
            await viewModel.FilterCommand.ExecuteAsync(ModFilterType.All);
            Assert.Equal(0xcd4238c6a0c69e32UL, viewModel.SelectedCategory);
            Assert.Equal(stored[0].Id, Assert.Single(viewModel.FilteredMods).Id);
            category.SelectedValue = 0xe0a48d0be9a7453fUL;
            await viewModel.FilterCommand.ExecuteAsync(ModFilterType.All);
            Assert.Equal(stored[0].Id, Assert.Single(viewModel.FilteredMods).Id);
            category.SelectedValue = ulong.MaxValue;
            await viewModel.FilterCommand.ExecuteAsync(ModFilterType.All);
            Assert.Equal(stored[1].Id, Assert.Single(viewModel.FilteredMods).Id);
            Assert.Contains(viewModel.CategoryOptions, option => option.Value == ulong.MaxValue);
            category.SelectedValue = 0xeac0b497876adedfUL;
            await viewModel.FilterCommand.ExecuteAsync(ModFilterType.All);
            Assert.Empty(viewModel.FilteredMods);
            Assert.True(viewModel.IsFilterEmptyStateVisible);
            await viewModel.ClearFiltersCommand.ExecuteAsync(null);
            Assert.Equal(3, viewModel.FilteredMods.Count);
            Assert.Null(category.SelectedValue);
            var sort = Assert.Single(selectors.Where(box => ReferenceEquals(box.ItemsSource, viewModel.SortModes)));
            sort.SelectedValue = ModSortMode.NameDescending;
            await viewModel.FilterCommand.ExecuteAsync(ModFilterType.All);
            Assert.Equal(ModSortMode.NameDescending, viewModel.CurrentSortMode);
            Assert.Equal(mods.OrderByDescending(mod => mod.Name).Select(mod => mod.Id),
                viewModel.FilteredMods.Select(mod => mod.Id));
        }
        finally { window.DataContext = null; view.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Navigation_ShouldSynchronizeNativeSelectionCommandsAndPages()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var viewModel = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            var pageHost = window.FindControl<TabControl>("PageHost")!;
            Assert.IsType<FluentAvaloniaTheme>(Avalonia.Application.Current!.Styles[0]);

            foreach (var item in navigation.MenuItems.OfType<FANavigationViewItem>())
            {
                var index = int.Parse(item.Tag!.ToString()!);
                navigation.SelectedItem = item;
                Assert.Equal(index, viewModel.SelectedTabIndex);
                Assert.Equal(index, pageHost.SelectedIndex);
            }

            viewModel.NavigateToLibraryCommand.Execute(null);
            Assert.Same(navigation.MenuItems[1], navigation.SelectedItem);
            viewModel.NavigateToProfilesCommand.Execute(null);
            Assert.Same(navigation.MenuItems[0], navigation.SelectedItem);
            viewModel.NavigateToSettingsCommand.Execute(null);
            Assert.Same(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "4"), navigation.SelectedItem);
            await viewModel.BrowseModsViewModel!.StopAsync();
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileSortPreview_ShouldShowMatchingDropTargetAndClearOnFinish()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        await profiles.CreateProfileAsync("配置二", "");
        await profiles.CreateProfileAsync("配置三", "");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var window = new MainWindow { DataContext = main };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().First();
            var list = view.FindControl<ItemsControl>("ModItemsControl")!;
            var profileList = view.FindControl<ListBox>("ProfileList")!;
            var marker = view.FindControl<Border>("SortInsertionMarker")!;
            var ghost = view.FindControl<Border>("SortDragGhost")!;
            var layer = view.FindControl<Canvas>("SortPreviewLayer")!;
            var title = view.FindControl<TextBlock>("SortDragTitle")!;
            var destination = view.FindControl<TextBlock>("SortDragDestination")!;
            Assert.False(layer.IsHitTestVisible);
            var mods = vm.VisibleProfileMods.ToArray();
            Assert.Equal(3, mods.Length);
            var source = list.ContainerFromIndex(2)!;
            var target = list.ContainerFromIndex(0)!;
            var y = target.TranslatePoint(default, list)!.Value.Y + 1;
            var movingIds = new List<Guid> { mods[2].ModId };
            var originalOpacity = source.Opacity;
            InvokeSortPreview(view, "BeginModDragPreview", movingIds);
            Assert.Equal(originalOpacity * 0.35, source.Opacity);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                InvokeSortPreview(view, "UpdateModDragPreview", movingIds, y);
                window.UpdateLayout();
                Assert.True(marker.IsVisible);
                Assert.True(ghost.IsVisible);
                Assert.Equal(mods[2].ModName, title.Text);
                Assert.Contains(mods[0].ModName, destination.Text!);
                Assert.InRange(Canvas.GetTop(marker), 0, layer.Bounds.Height);
                Capture(window, theme.ToString(), "profile-sort-preview");
            }
            var (_, anchor) = ((int, Guid?))InvokeSortPreview(view, "GetModDropTarget", movingIds, y)!;
            Assert.Equal(mods[0].ModId, anchor);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(mods[2].ModId.ToString()));
            var visibilityChanges = 0;
            EventHandler<AvaloniaPropertyChangedEventArgs> watchVisibility = (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty) visibilityChanges++;
            };
            ghost.PropertyChanged += watchVisibility;
            try
            {
                for (var step = 1; step <= 10; step++)
                {
                    target.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, target, new Point(8, step + 12), KeyModifiers.None));
                    Assert.True(ghost.IsVisible);
                    target.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(8, step + 12), KeyModifiers.None));
                    Assert.True(ghost.IsVisible);
                }
                Assert.Equal(0, visibilityChanges);
                target.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, target, new Point(-2000, -2000), KeyModifiers.None));
                Assert.False(ghost.IsVisible);
            }
            finally { ghost.PropertyChanged -= watchVisibility; }
            InvokeSortPreview(view, "HideSortPreview");
            Assert.False(marker.IsVisible);
            Assert.False(ghost.IsVisible);
            Assert.Equal(originalOpacity * 0.35, source.Opacity);
            InvokeSortPreview(view, "UpdateModDragPreview", movingIds, y);
            Assert.True(ghost.IsVisible);
            InvokeSortPreview(view, "ClearSortPreview");
            Assert.Equal(originalOpacity, source.Opacity);
            Assert.False(ghost.IsVisible);
            await vm.MoveModsAsync(movingIds, anchor);
            Assert.Equal(mods[2].ModId, vm.ProfileMods[0].ModId);
            Assert.Equal(mods[2].ModId, (await profiles.GetProfileByIdAsync(vm.SelectedProfile!.Id))!.Mods.OrderBy(mod => mod.Order).First().ModId);

            window.UpdateLayout();
            movingIds = vm.VisibleProfileMods.Take(2).Select(mod => mod.ModId).ToList();
            InvokeSortPreview(view, "BeginModDragPreview", movingIds);
            var separator = (Control)list.ContainerFromIndex(vm.ProfileRows.Count - 1)!;
            var separatorY = separator.TranslatePoint(new Point(0, 0), list)!.Value.Y;
            InvokeSortPreview(view, "UpdateModDragPreview", movingIds, separatorY);
            Assert.Contains("2 个模组", title.Text!);
            Assert.Equal("松开放到 BSL 分隔符之前", destination.Text);
            var (_, endAnchor) = ((int, Guid?))InvokeSortPreview(view, "GetModDropTarget", movingIds, separatorY)!;
            Assert.Equal(Guid.Empty, endAnchor);
            InvokeSortPreview(view, "ClearSortPreview");

            var profile = vm.Profiles[1];
            var (_, profileTarget) = ((int, int))InvokeSortPreview(view, "GetProfileDropTarget", profile.Id, profileList.Bounds.Height + 10)!;
            Assert.Equal(2, profileTarget);
            InvokeSortPreview(view, "BeginProfileDragPreview", profile.Id);
            InvokeSortPreview(view, "UpdateProfileDragPreview", profile.Id, profileList.Bounds.Height + 10);
            Assert.Equal(profile.Name, title.Text);
            Assert.True(ghost.IsVisible);
            var (_, fixedTarget) = ((int, int))InvokeSortPreview(view, "GetProfileDropTarget", profile.Id, 0d)!;
            Assert.Equal(1, fixedTarget);
            InvokeSortPreview(view, "UpdateProfileDragPreview", vm.Profiles[0].Id, 0d);
            Assert.False(ghost.IsVisible);
            InvokeSortPreview(view, "ClearSortPreview");
            await vm.MoveProfileAsync(profile.Id, profileTarget);
            Assert.Same(profile, vm.Profiles[2]);

            InvokeSortPreview(view, "BeginModDragPreview", movingIds);
            InvokeSortPreview(view, "UpdateModDragPreview", movingIds, list.Bounds.Height + 10);
            window.DataContext = null;
            view.DataContext = null;
            Assert.False(ghost.IsVisible);
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    private static object? InvokeSortPreview(ProfileView view, string method, params object[] arguments)
        => typeof(ProfileView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments);

    [AvaloniaFact]
    public async Task Conflicts_ShouldFollowViewedProfileAndClearStaleResults()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var initial = (await profiles.GetActiveProfileAsync())!;
        var otherId = await profiles.CreateProfileAsync("另一套配置");
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        Assert.Equal(initial.Id, main.ProfileViewModel.SelectedProfile!.Id);
        Assert.True(main.ProfileViewModel.Profiles.First().IsDefault);
        main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(profile => profile.Id == otherId);
        await main.ProfileViewModel.RetryProfileModsCommand.ExecuteAsync(null);
        Assert.Equal(initial.Id, (await profiles.GetActiveProfileAsync())!.Id);
        Assert.Equal("另一套配置", main.ConflictsViewModel.ProfileName);
        await main.ConflictsViewModel.ScanAsync();
        Assert.True(main.ConflictsViewModel.HasScanned);
        Assert.Contains("已扫描 0 个", main.ConflictsViewModel.SummaryText);
        await profiles.AddModToProfileAsync(otherId, initial.Mods[0].ModId);
        Assert.False(main.ConflictsViewModel.HasScanned);
        Assert.Empty(main.ConflictsViewModel.Groups);
        await main.ConflictsViewModel.ScanAsync();
        Assert.Contains("已扫描 1 个", main.ConflictsViewModel.SummaryText);
        await main.ProfileViewModel.SetActiveProfileAsync(main.ProfileViewModel.Profiles.Single(profile => profile.Id == otherId));
        var nextLaunch = scope.Services.GetRequiredService<MainWindowViewModel>();
        await nextLaunch.InitializeAsync();
        Assert.Equal(otherId, nextLaunch.ProfileViewModel.SelectedProfile!.Id);
        Assert.Equal("另一套配置", nextLaunch.ConflictsViewModel.ProfileName);
    }

    [AvaloniaFact]
    public async Task ConflictDetails_ShouldVirtualizeLargeResourceAndOwnerLists()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ConflictsViewModel;
        var owners = Enumerable.Range(0, 2000).Select(index => new ConflictFileOwner
            { ModId = Guid.NewGuid(), ModName = $"HD2Mimnt Test Owner {index}", FullPath = "sample.patch_0" }).ToArray();
        vm.Groups = new(Enumerable.Range(0, 10000).Select(index => new ConflictGroupViewModel(new ConflictGroup
            { ResourceId = (ulong)index, TypeId = 0xcd4238c6a0c69e32UL, Owners = index == 0 ? owners : owners.Take(2).ToArray() })));
        vm.HasScanned = true;
        main.SelectedTabIndex = 2;
        main.NotificationPanelViewModel.DismissAllToasts();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            Layout();
            var view = window.GetVisualDescendants().OfType<ConflictsView>().Single();
            var list = view.FindControl<ListBox>("ConflictGroupsList")!;
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            Assert.True(list.IsEffectivelyVisible);
            Assert.InRange(list.GetRealizedContainers().Count(), 1, 30);
            Assert.Null(list.ContainerFromIndex(9999));
            var ownerList = list.GetVisualDescendants().OfType<ListBox>().First();
            Assert.InRange(ownerList.GetRealizedContainers().Count(), 1, 30);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Layout();
                Capture(window, theme.ToString(), "large-conflict-details");
            }
            list.ScrollIntoView(vm.Groups[^1]);
            Layout();
            Assert.NotNull(list.ContainerFromIndex(9999));
            Assert.InRange(list.GetRealizedContainers().Count(), 1, 30);
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task AutomaticConflicts_ShouldSaveDefaultOffDebounceChangesAndStopOnClose()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var data = scope.Services.GetRequiredService<TestDataService>();
        var mods = (await data.GetAllModsAsync()).ToArray();
        foreach (var mod in mods)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0")));
            writer.Write(0xF0000011u); writer.Write(1u); writer.Write(1u);
            writer.Write(new byte[60 + 32]); writer.Write(42UL); writer.Write(0xcd4238c6a0c69e32UL);
            writer.Write(new byte[64]); // 零长度正文，仅需索引。
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var settings = main.SettingsViewModel;
        var conflicts = main.ConflictsViewModel;
        var completed = 0;
        conflicts.ScanCompleted += (_, _) => completed++;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            main.SelectedTabIndex = 4;
            settings.SelectedCategory = "配置文件";
            main.NotificationPanelViewModel.DismissAllToasts();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            void Click(Control control)
            {
                Layout();
                using var frame = window.CaptureRenderedFrame();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            async Task WaitForScan(int count)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (completed < count || conflicts.IsScanning) await Task.Delay(20, timeout.Token);
                Layout();
            }
            Layout();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var toggle = view.FindControl<ToggleSwitch>("AutoCheckConflictsToggle")!;
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            Assert.False(settings.AutoCheckConflicts);
            Assert.False(toggle.IsChecked);
            await Task.Delay(900);
            Assert.Equal(0, completed);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Layout();
                Assert.True(toggle.IsEffectivelyVisible && toggle.Bounds.Width > 0);
                Capture(window, theme.ToString(), "automatic-conflict-settings");
            }
            Click(toggle);
            Assert.True(settings.AutoCheckConflicts);
            await Task.Delay(900); // 修改草稿不会提前启用后台检查，保存后才生效。
            Assert.Equal(0, completed);
            var save = view.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "保存设置");
            Click(save);
            await settings.SaveCommand.ExecutionTask!;
            Assert.True(await scope.Services.GetRequiredService<SettingsService>().GetSettingAsync(SettingKeys.ProfilesAutoCheckConflicts, false));
            await WaitForScan(1);
            Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.True(mod.HasConflict));
            Assert.Equal(4, main.SelectedTabIndex);
            var profiles = scope.Services.GetRequiredService<ProfileService>();
            var id = main.ProfileViewModel.SelectedProfile!.Id;
            var existingResults = conflicts.Groups;
            var separator = await profiles.AddSeparatorAsync(id, "显示分组", 0);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!main.ProfileViewModel.ProfileSeparators.Any(row => row.ModId == separator.Id))
                    await Task.Delay(20, timeout.Token);
            var separatorRow = main.ProfileViewModel.ProfileSeparators.Single(row => row.ModId == separator.Id);
            await main.ProfileViewModel.ToggleSeparatorCommand.ExecuteAsync(separatorRow);
            Assert.True(separatorRow.IsCollapsed);
            await main.ProfileViewModel.ToggleSeparatorCommand.ExecuteAsync(separatorRow);
            Assert.False(separatorRow.IsCollapsed);
            await profiles.SetSeparatorCollapsedAsync(id, Guid.Empty, true);
            await profiles.SetSeparatorCollapsedAsync(id, Guid.Empty, false);
            await profiles.UpdateSeparatorAsync(id, separator.Id, "显示分组 · 改名", "#12AB56");
            await profiles.RemoveSeparatorAsync(id, separator.Id);
            await Task.Delay(1000);
            Layout();
            Assert.Equal(1, completed);
            Assert.True(conflicts.HasScanned);
            Assert.Same(existingResults, conflicts.Groups);
            Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.True(mod.HasConflict));
            for (var index = 0; index < 6; index++)
                await profiles.SetModEnabledAsync(id, mods[0].Id, index % 2 != 0);
            await WaitForScan(2);
            await Task.Delay(900);
            Assert.Equal(2, completed);
            // 正在进行的结果失效后，旧扫描不能回填卡片。
            var obsolete = conflicts.ScanCommand.ExecuteAsync(null);
            conflicts.InvalidateResults();
            conflicts.InvalidateResults();
            await obsolete;
            await WaitForScan(3);
            Assert.Equal(3, completed);
            conflicts.InvalidateResults();
            main.NotificationPanelViewModel.DismissAllToasts();
            Click(toggle);
            Assert.False(settings.AutoCheckConflicts);
            await settings.SaveCommand.ExecuteAsync(null);
            await Task.Delay(900);
            Assert.Equal(3, completed);
            Assert.False(conflicts.IsScanning);
            // 关闭自动检查后，手动检查仍可用。
            await main.ScanProfileConflictsCommand.ExecuteAsync(null);
            Assert.Equal(4, completed);
            settings.AutoCheckConflicts = true;
            await settings.SaveCommand.ExecuteAsync(null);
            Assert.True(await main.CanCloseAsync());
            await Task.Delay(900);
            Assert.Equal(4, completed);
            Assert.False(conflicts.IsScanning);
        }
        finally
        {
            await conflicts.StopScanAsync();
            app.RequestedThemeVariant = originalTheme;
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ConflictBadge_ShouldChooseTargetRevealGroupAndScrollToCard()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var data = scope.Services.GetRequiredService<TestDataService>();
        await data.ImportGeneratedModsAsync(Enumerable.Range(1, 24).Select(index => new Mod
        {
            Name = $"HD2Mimnt Test Navigation {index:00}", Author = "Helldivers", Version = "1.0.0",
            Path = Path.Combine(scope.Root, "navigation", index.ToString())
        }));
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profile = (await profiles.GetActiveProfileAsync())!;
        foreach (var mod in (await data.GetAllModsAsync()).Where(mod => !profile.Mods.Any(item => item.ModId == mod.Id)))
            await profiles.AddModToProfileAsync(profile.Id, mod.Id);
        var separator = await profiles.AddSeparatorAsync(profile.Id, "冲突目标分组", 3);
        await profiles.SetSeparatorCollapsedAsync(profile.Id, separator.Id, true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var source = vm.ProfileMods[0];
        var middle = vm.ProfileMods[15];
        var last = vm.ProfileMods[^1];
        ConflictGroupViewModel Group(params ProfileModViewModel[] owners) => new(new ConflictGroup
        {
            ResourceId = 42, TypeId = 0xcd4238c6a0c69e32UL,
            Owners = owners.Select(mod => new ConflictFileOwner { ModId = mod.ModId, ModName = mod.ModName }).ToArray()
        });
        var overlap = Group(source, middle, last);
        vm.ApplyConflictResults([overlap, overlap]);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var scroll = view.FindControl<ScrollViewer>("ModScrollViewer")!;
            var items = view.FindControl<ItemsControl>("ModItemsControl")!;
            Button Badge() => view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "NavigateConflictButton" && button.DataContext == source && button.IsEffectivelyVisible);
            void Click(Button button)
            {
                Layout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(button.Command);
                Assert.True(button.IsEffectivelyEnabled);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(point) as Visual;
                Assert.True(hit == button || hit?.GetVisualAncestors().Contains(button) == true,
                    $"{button.Name} 中心 {point} 命中 {hit?.GetType().Name} / {(hit as Control)?.Name}");
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                Assert.True(button.IsPressed, $"未按下 {button.Name}，命中 {hit?.GetType().Name}");
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            vm.ToggleModSelected(source);
            vm.ToggleModSelected(vm.ProfileMods[1]);
            Layout();
            Click(Badge());
            await vm.OpenConflictNavigationCommand.ExecutionTask!;
            Layout();
            Assert.True(vm.IsConflictPickerOpen);
            Assert.Equal(new[] { middle.ModId, last.ModId }, vm.ConflictNavigationChoices.Select(mod => mod.ModId));
            Assert.Equal(2, vm.SelectedModCount);
            Assert.DoesNotContain(last, vm.VisibleProfileMods);
            var picker = view.FindControl<Border>("ConflictModPickerSurface")!;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Layout();
                Assert.True(picker.IsEffectivelyVisible && picker.Bounds.Height > 100);
                Assert.Equal(2, picker.GetVisualDescendants().OfType<Button>().Count(button => button.CommandParameter is ProfileModViewModel));
                Capture(window, theme.ToString(), "conflict-mod-picker");
            }
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Layout();
            Assert.False(vm.IsConflictPickerOpen);
            Assert.Equal(2, vm.SelectedModCount);
            Click(Badge());
            await vm.OpenConflictNavigationCommand.ExecutionTask!;
            Layout();
            Click(picker.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter == last));
            await vm.LocateConflictModCommand.ExecutionTask!;
            await Task.Delay(50);
            Layout();
            Assert.False(vm.IsConflictPickerOpen);
            Assert.False(vm.ProfileSeparators.Single().IsCollapsed);
            Assert.False((await profiles.GetProfileByIdAsync(profile.Id))!.Separators.Single().IsCollapsed);
            Assert.Same(last, vm.FocusedMod);
            Assert.True(last.IsConflictLocation && last.IsFocused);
            Assert.Equal(0, vm.SelectedModCount);
            void AssertTargetInView()
            {
                Assert.True(scroll.Offset.Y > 0);
                var container = Assert.IsAssignableFrom<Control>(items.ContainerFromIndex(vm.ProfileRows.IndexOf(last)));
                var top = container.TranslatePoint(default, scroll)!.Value.Y;
                Assert.True(top >= -1 && top + container.Bounds.Height <= scroll.Bounds.Height + 1,
                    $"目标未完全显示：top={top}，height={container.Bounds.Height}，viewport={scroll.Bounds.Height}");
                Assert.Contains(view.GetVisualDescendants().OfType<Border>(), card => card.DataContext == last && card.Classes.Contains("conflict-location"));
            }
            AssertTargetInView();
            Capture(window, "Dark", "conflict-mod-located");
            // 只有一个关联模组时直接定位，同时清除遮住目标的搜索。
            vm.ApplyConflictResults([Group(source, last)]);
            vm.ModSearchText = source.ModName;
            scroll.Offset = default;
            Layout();
            Assert.DoesNotContain(last, vm.VisibleProfileMods);
            Assert.Single(vm.VisibleProfileMods);
            Assert.False(vm.HasNoModResults);
            Assert.True(scroll.IsEffectivelyVisible);
            Click(Badge());
            await vm.OpenConflictNavigationCommand.ExecutionTask!;
            await Task.Delay(50);
            Layout();
            Assert.Equal(string.Empty, vm.ModSearchText);
            Assert.False(vm.IsConflictPickerOpen);
            AssertTargetInView();
            Assert.Equal(0, main.SelectedTabIndex);
            vm.ApplyConflictResults([overlap]);
            scroll.Offset = default;
            Layout();
            Click(Badge());
            await vm.OpenConflictNavigationCommand.ExecutionTask!;
            Assert.True(vm.IsConflictPickerOpen);
            vm.ApplyConflictResults([]);
            Assert.False(vm.IsConflictPickerOpen);
            Assert.Empty(vm.ConflictNavigationChoices);
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileConflicts_ShouldScanInPlaceMarkCardsAndClearStaleBadges()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var mods = (await scope.Services.GetRequiredService<TestDataService>().GetAllModsAsync()).ToArray();
        void WriteResource(Mod mod, string name, uint size)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(mod.Path, name)));
            writer.Write(0xF0000011u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(new byte[60]);
            writer.Write(new byte[32]);
            writer.Write(ulong.MaxValue);
            writer.Write(0xcd4238c6a0c69e32UL);
            writer.Write(184UL);
            writer.Write(new byte[32]);
            writer.Write(size);
            writer.Write(new byte[20]);
            writer.Write(new byte[size]);
        }
        WriteResource(mods[0], "aaaaaaaaaaaaaaaa.patch_0", 48);
        WriteResource(mods[1], "bbbbbbbbbbbbbbbb.patch_0", 64);
        File.WriteAllText(Path.Combine(mods[2].Path, "cccccccccccccccc.patch_0"), "broken");
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var activeId = (await profiles.GetActiveProfileAsync())!.Id;
        var viewedId = await profiles.CreateProfileAsync("日常装备 · 冲突检查");
        foreach (var mod in mods) await profiles.AddModToProfileAsync(viewedId, mod.Id);
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(profile => profile.Id == viewedId);
        await main.ProfileViewModel.RetryProfileModsCommand.ExecuteAsync(null);
        scope.Services.GetRequiredService<NotificationService>().Clear();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            main.NavigateToProfilesCommand.Execute(null);
            main.NotificationPanelViewModel.DismissAllToasts();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var button = view.FindControl<Button>("ScanProfileConflictsButton")!;
            Assert.Same(main.ScanProfileConflictsCommand, button.Command);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(),
                badge => badge.Name == "ProfileConflictBadge" && badge.IsEffectivelyVisible);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Assert.True(button.IsPressed);
            window.MouseUp(point, MouseButton.Left);
            var pending = main.ScanProfileConflictsCommand.ExecutionTask;
            Assert.NotNull(pending);
            await pending!;
            Layout();
            Assert.Equal(0, main.SelectedTabIndex);
            Assert.Equal(activeId, (await profiles.GetActiveProfileAsync())!.Id);
            Assert.Equal("日常装备 · 冲突检查", main.ConflictsViewModel.ProfileName);
            Assert.True(main.ConflictsViewModel.HasScanned);
            Assert.Contains("检测不完整", view.FindControl<TextBlock>("ProfileConflictStatus")!.Text);
            var flagged = main.ProfileViewModel.ProfileMods.Where(mod => mod.HasConflict).ToArray();
            Assert.Equal(2, flagged.Length);
            Assert.All(flagged, mod =>
            {
                var other = flagged.Single(other => other.ModId != mod.ModId);
                Assert.Equal($"与以下模组存在覆盖冲突：\n{other.ModName}\n\n详细信息请查看“冲突”页。", mod.ConflictTooltip);
            });
            // 名称较长且带选项时，也不能挤掉警告和右侧操作。
            flagged[0].ModName = "超级地球装备外观组合 · 很长的模组名称用于检查省略和提示布局";
            flagged[0].HasOptions = true;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 1100, 900 })
            {
                app.RequestedThemeVariant = theme;
                window.Width = width;
                Layout();
                main.NotificationPanelViewModel.DismissAllToasts();
                Layout();
                var badges = view.GetVisualDescendants().OfType<Border>()
                    .Where(badge => badge.Name == "ProfileConflictBadge" && badge.IsEffectivelyVisible).ToArray();
                Assert.Equal(2, badges.Length);
                foreach (var badge in badges)
                {
                    Assert.Equal(20, badge.Bounds.Width);
                    Assert.Equal(20, badge.Bounds.Height);
                    var icon = badge.GetVisualDescendants().OfType<HD2Mimnt.UI.Controls.UtilityIcon>().Single();
                    Assert.Equal("Warning", icon.Kind);
                    var imageSlot = Assert.IsType<Panel>(badge.Parent);
                    Assert.Equal(52, imageSlot.Bounds.Width);
                    Assert.Equal(52, imageSlot.Bounds.Height);
                    Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, badge.HorizontalAlignment);
                    Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, badge.VerticalAlignment);
                    var card = badge.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("mod-card"));
                    var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == ((ProfileModViewModel)badge.DataContext!).ModName);
                    var toggle = card.GetVisualDescendants().OfType<ToggleSwitch>().Single();
                    var badgeRight = badge.TranslatePoint(new Point(badge.Bounds.Width, 0), card)!.Value.X;
                    Assert.True(badgeRight < title.TranslatePoint(default, card)!.Value.X);
                    Assert.True(badgeRight < toggle.TranslatePoint(default, card)!.Value.X);
                }
                Capture(window, theme.ToString(), $"profile-conflicts-{width}");
                if (theme == ThemeVariant.Dark && width == 1100)
                {
                    var badge = badges.First();
                    ToolTip.SetShowDelay(badge, 0);
                    var hover = badge.TranslatePoint(new Point(10, 10), window)!.Value;
                    window.MouseMove(hover);
                    await Task.Delay(100);
                    Layout();
                    Assert.True(ToolTip.GetIsOpen(badge));
                    Capture(window, theme.ToString(), "profile-conflict-tooltip");
                    window.MouseMove(new Point(260, 600));
                    Layout();
                }
            }
            // 详情页共用同一结果，回到配置页或重建卡片不需要再扫描。
            main.NavigateToConflictsCommand.Execute(null);
            Assert.Single(main.ConflictsViewModel.Groups);
            main.NavigateToProfilesCommand.Execute(null);
            await main.ProfileViewModel.RetryProfileModsCommand.ExecuteAsync(null);
            Assert.Equal(2, main.ProfileViewModel.ProfileMods.Count(mod => mod.HasConflict));
            await profiles.SetModEnabledAsync(viewedId, mods[0].Id, false);
            Assert.False(main.ConflictsViewModel.HasScanned);
            Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.False(mod.HasConflict));
            await main.ScanProfileConflictsCommand.ExecuteAsync(null);
            Assert.True(main.ConflictsViewModel.HasScanned);
            Assert.Empty(main.ConflictsViewModel.Groups);
            Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.False(mod.HasConflict));
            await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.DefaultDeploymentOrder, "BottomToTop");
            Assert.False(main.ConflictsViewModel.HasScanned);
            main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(profile => profile.Id == activeId);
            await main.ProfileViewModel.RetryProfileModsCommand.ExecuteAsync(null);
            Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.False(mod.HasConflict));
            Assert.Equal(0, main.SelectedTabIndex);
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Conflicts_ShouldRenderResourceDifferencesAndIncompleteScanThroughActualButton()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var mods = (await scope.Services.GetRequiredService<TestDataService>().GetAllModsAsync()).ToArray();
        void WriteResource(Mod mod, string name, uint size)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(mod.Path, name)));
            writer.Write(0xF0000011u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(new byte[60]);
            writer.Write(new byte[32]);
            writer.Write(ulong.MaxValue);
            writer.Write(0xcd4238c6a0c69e32UL);
            writer.Write(184UL);
            writer.Write(new byte[32]);
            writer.Write(size);
            writer.Write(new byte[20]);
            writer.Write(new byte[size]);
        }
        WriteResource(mods[0], "aaaaaaaaaaaaaaaa.patch_0", 48);
        WriteResource(mods[1], "bbbbbbbbbbbbbbbb.patch_0", 64);
        File.WriteAllText(Path.Combine(mods[2].Path, "cccccccccccccccc.patch_0"), "broken");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        scope.Services.GetRequiredService<NotificationService>().Clear();
        main.NavigateToConflictsCommand.Execute(null);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            main.NavigateToConflictsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            main.NotificationPanelViewModel.DismissAllToasts();
            window.UpdateLayout();
            var view = window.GetVisualDescendants().OfType<ConflictsView>().Single();
            var button = view.FindControl<Button>("ScanConflictsButton")!;
            Assert.Same(main.ConflictsViewModel.ScanCommand, button.Command);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.True(button.Bounds.Width > 0);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            var hit = window.InputHitTest(point) as Visual;
            Assert.True(hit == button || hit?.GetVisualAncestors().Contains(button) == true,
                $"按钮中心 {point} 命中 {hit?.GetType().Name}；窗口 {window.Bounds}，按钮 {button.Bounds}");
            using var frame = window.CaptureRenderedFrame();
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Assert.True(button.IsPressed, "鼠标按下未到达扫描按钮");
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var pending = main.ConflictsViewModel.ScanCommand.ExecutionTask;
            Assert.NotNull(pending);
            await pending!;
            Assert.True(main.ConflictsViewModel.HasScanned);
            Assert.True(main.ConflictsViewModel.HasWarnings);
            Assert.True(Assert.Single(main.ConflictsViewModel.Groups).IsDefiniteConflict);
            Assert.Contains("检测不完整", main.ConflictsViewModel.SummaryText);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var visibleText = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).ToArray();
                Assert.Contains(visibleText, text => text.Text == "明确差异" && text.Bounds.Width > 0);
                Assert.Contains(visibleText, text => text.Text == "预期生效" && text.Bounds.Width > 0);
                Assert.Contains(visibleText, text => text.Text == "0xFFFFFFFFFFFFFFFF");
                Assert.Contains(visibleText, text => text.Text == "纹理");
                Assert.Contains(visibleText, text => text.Text?.Contains("检测不完整：") == true);
            }
            var profile = (await scope.Services.GetRequiredService<ProfileService>().GetActiveProfileAsync())!;
            await scope.Services.GetRequiredService<ProfileService>().SetModEnabledAsync(profile.Id, mods[0].Id, false);
            Assert.False(main.ConflictsViewModel.HasScanned);
            Assert.Empty(main.ConflictsViewModel.Groups);
            Assert.Empty(main.ConflictsViewModel.Warnings);
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public void ThemeResources_ShouldFollowThemeAndKeepTransparencyBindings()
    {
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var normalButton = new Button { Content = "普通操作" };
        var window = new Window
        {
            Content = new StackPanel
            {
                Children =
                {
                    normalButton,
                    new Button { Content = "主要操作", Classes = { "accent" } },
                    new Button { Content = "删除", Classes = { "danger" } }
                }
            }
        };
        try
        {
            window.Show();
            app.Resources["CardSurfaceOpacity"] = 0.65;
            app.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var darkCard = Brush(window, "CardFillBrush").Color;
            Assert.Equal((Color)window.FindResource(window.ActualThemeVariant, "SolidBackgroundFillColorTertiary")!, darkCard);
            Assert.Equal(0.65, Brush(window, "CardFillBrush").Opacity);
            var darkText = Brush(window, "TextPrimaryBrush").Color;
            Assert.Equal(darkText, Assert.IsAssignableFrom<ISolidColorBrush>(normalButton.Foreground).Color);

            app.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(darkCard, Brush(window, "CardFillBrush").Color);
            Assert.NotEqual(darkText, Brush(window, "TextPrimaryBrush").Color);
            Assert.Equal(Brush(window, "TextPrimaryBrush").Color, Assert.IsAssignableFrom<ISolidColorBrush>(normalButton.Foreground).Color);
            Assert.Equal(0.65, Brush(window, "CardFillBrush").Opacity);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Default })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal((Color)window.FindResource(window.ActualThemeVariant, "SystemFillColorSuccess")!, Brush(window, "SuccessBrush").Color);
                Assert.Equal((Color)window.FindResource(window.ActualThemeVariant, "SystemFillColorCritical")!, Brush(window, "ErrorBrush").Color);
            }
            var themeProvider = Assert.IsType<FluentAvaloniaTheme>(app.Styles[0]);
            Assert.False(themeProvider.PreferSystemTheme);
        }
        finally
        {
            window.Close();
            app.Resources["CardSurfaceOpacity"] = 1.0;
            app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task Pages_ShouldRenderInBothThemesAtDefaultAndMinimumSizes()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var viewModel = scope.Services.GetRequiredService<MainWindowViewModel>();
        await viewModel.InitializeAsync();
        var window = new MainWindow { DataContext = viewModel };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            foreach (var size in new[] { new Size(1280, 800), new Size(960, 600) })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                viewModel.SettingsViewModel.Theme = theme.ToString();
                for (var index = 0; index < 5; index++)
                {
                    viewModel.SelectedTabIndex = index;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var pageHost = window.FindControl<TabControl>("PageHost")!;
                    Assert.True(pageHost.Bounds.Width > size.Width - 80);
                    Assert.True(pageHost.Bounds.Height > 400);
                    Assert.Equal(index, pageHost.SelectedIndex);
                    Capture(window, theme.ToString(), $"{size.Width:0}-{index}");

                    if (index == 4)
                    {
                        foreach (var category in viewModel.SettingsViewModel.SettingsCategories)
                        {
                            viewModel.SettingsViewModel.SelectedCategory = category;
                            Dispatcher.UIThread.RunJobs();
                            window.UpdateLayout();
                            Assert.Contains(window.GetVisualDescendants().OfType<FASettingsExpander>(), expander => expander.IsEffectivelyVisible);
                        }
                        viewModel.SettingsViewModel.SelectedCategory = "外观";
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Capture(window, theme.ToString(), $"{size.Width:0}-appearance");
                    }
                }
            }
            scope.Services.GetRequiredService<NotificationService>().ShowSuccess("部署已完成", "配置文件");
            viewModel.OpenNotificationsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<Border>(),
                card => card.Classes.Contains("notification-card") && card.IsEffectivelyVisible);
            Capture(window, "Light", "notifications");

            // 背景图启用时，导航容器不能阻断内容区的透明度设置。
            var backgroundPath = Path.Combine(scope.Root, "background.png");
            using (var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96)))
                bitmap.Save(backgroundPath);
            viewModel.SettingsViewModel.BackgroundImagePath = backgroundPath;
            viewModel.SettingsViewModel.CardOpacity = 60;
            await viewModel.ApplyAppearanceAsync();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            Assert.True(viewModel.HasBackgroundImage);
            Assert.Equal(0.0, Brush(window, "ContentFillBrush").Opacity);
            Assert.Equal(0.6, Brush(window, "CardFillBrush").Opacity);
            Assert.Same(window.FindResource("ContentFillBrush"), navigation.FindResource("NavigationViewContentBackground"));
            Assert.Equal(0.6, Assert.IsAssignableFrom<ISolidColorBrush>(navigation.FindResource("NavigationViewTopPaneBackground")).Opacity);
            viewModel.SettingsViewModel.BackgroundImagePath = string.Empty;
            viewModel.SettingsViewModel.CardOpacity = 100;
            await viewModel.ApplyAppearanceAsync();
            Assert.Equal(1.0, Brush(window, "ContentFillBrush").Opacity);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task Confirmation_ShouldUseNativeDialogAndDefaultToCancel()
    {
        var window = new Window { Width = 640, Height = 480 };
        try
        {
            window.Show();
            var service = new ConfirmationService();
            service.SetOwner(window);
            var pending = service.ConfirmAsync("删除模组", "确认删除选中的模组？");
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
            Assert.Equal(FAContentDialogButton.Close, dialog.DefaultButton);
            dialog.Hide();
            Assert.False(await pending);
        }
        finally { window.Close(); }
    }

    private static ISolidColorBrush Brush(Window window, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(key));

    [AvaloniaFact]
    public void PermissionDialog_ShouldRenderSharedIconsAndWarningInBothThemes()
    {
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var viewModel = new PluginPermissionDialogViewModel();
        viewModel.Initialize("测试插件", "1.0.0", "Helldivers", "用于扩展模组管理器功能", "FullAccess",
            new[] { "ReadModFiles", "WriteModFiles", "AccessNetwork", "ExecuteProcesses" });
        var dialog = new PluginPermissionDialog { DataContext = viewModel };
        try
        {
            dialog.Show();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                Assert.Contains(dialog.GetVisualDescendants().OfType<FAInfoBar>(),
                    bar => bar.IsOpen && bar.Severity == FAInfoBarSeverity.Warning);
                Assert.Contains(dialog.GetVisualDescendants().OfType<FASymbolIcon>(),
                    icon => icon.Symbol == FASymbol.Globe);
                Capture(dialog, theme.ToString(), "permissions");
            }
        }
        finally { dialog.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    private static void CaptureSingleClickMarker(Window window, string name)
    {
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Capture(window, theme.ToString(), name);
            }
        }
        finally { app.RequestedThemeVariant = originalTheme; }
    }

    private static void Capture(Window window, string theme, string name)
    {
        var output = Environment.GetEnvironmentVariable("HD2MIMNT_UI_PREVIEW_PATH");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(output, $"{theme}-{name}.png"));
    }

    private sealed class RecordingWebsiteLauncher : IWebsiteDownloadLauncher, IBrowserLauncher
    {
        public List<string> Urls { get; } = new();
        public bool FailNext { get; set; }
        public void Open(string url)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("测试浏览器无法打开"); }
            Urls.Add(url);
        }
    }

    private sealed class UiScope : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-FluentUi-{Guid.NewGuid():N}");
        public string Root => _root;
        public RecordingWebsiteLauncher WebsiteLauncher { get; } = new();
        public ServiceProvider Services { get; private set; } = null!;

        public static async Task<UiScope> CreateAsync(bool seedMods = false, IConfirmationService? confirmation = null,
            HttpMessageHandler? nexusHandler = null, HD2Mimnt.Core.Interfaces.ISystemLanguageProvider? systemLanguage = null,
            HD2Mimnt.Core.Interfaces.IMinidumpAnalyzer? minidumpAnalyzer = null,
            HD2Mimnt.Core.Interfaces.IDirectDownloadClientFactory? directDownloads = null)
        {
            var scope = new UiScope();
            var originalPath = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
            try
            {
                Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", scope._root);
                var services = new ServiceCollection();
                typeof(App).GetMethod("ConfigureServices", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Avalonia.Application.Current, new object[] { services });
                services.AddSingleton<IWebsiteDownloadLauncher>(scope.WebsiteLauncher);
                services.AddSingleton<IBrowserLauncher>(scope.WebsiteLauncher);
                if (systemLanguage != null) services.AddSingleton(systemLanguage);
                if (minidumpAnalyzer != null) services.AddSingleton(minidumpAnalyzer);
                if (directDownloads != null) services.AddSingleton(directDownloads);
                if (confirmation != null) services.AddSingleton<IConfirmationService>(confirmation);
                if (nexusHandler != null)
                {
                    services.AddKeyedSingleton<HttpClient>("nexus-api", (_, _) => new HttpClient(nexusHandler, disposeHandler: false));
                    services.AddKeyedSingleton<HttpClient>("downloads", (_, _) => new HttpClient(nexusHandler, disposeHandler: false));
                    services.AddKeyedSingleton<HttpClient>("browse-images", (_, _) => new HttpClient(nexusHandler, disposeHandler: false));
                }
                scope.Services = services.BuildServiceProvider();
            }
            finally { Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", originalPath); }

            try
            {
                await scope.Services.GetRequiredService<HD2MimntDbContext>().InitializeDatabaseAsync();
                await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.GameAutoDetect, false);
                if (seedMods)
                {
                    var testData = scope.Services.GetRequiredService<TestDataService>();
                    await testData.ImportGeneratedModsAsync(Enumerable.Range(1, 3).Select(index => new Mod
                    {
                        Name = $"HD2Mimnt Test Fluent {index}", Author = "Helldivers", Version = "1.0.0",
                        Description = "模组外观与功能测试", Path = Path.Combine(scope._root, "sources", index.ToString())
                    }));
                    var profiles = scope.Services.GetRequiredService<ProfileService>();
                    var profileId = await profiles.CreateProfileAsync("Default Profiles", "日常使用的模组组合");
                    await profiles.SetActiveProfileAsync(profileId);
                    foreach (var mod in await testData.GetAllModsAsync())
                        await profiles.AddModToProfileAsync(profileId, mod.Id);
                }
                return scope;
            }
            catch { await scope.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
