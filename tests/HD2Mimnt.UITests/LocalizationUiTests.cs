using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task LanguageSettings_ShouldPersistImmediatelyAndRefreshExistingPages()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true, systemLanguage: new TestSystemLanguage());
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.SettingsViewModel;
        vm.GamePath = "invalid-game-path";
        main.NavigateToSettingsCommand.Execute(null);
        vm.SelectedCategory = "外观";
        var window = new MainWindow { DataContext = main };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var input = settings.FindControl<ComboBox>("LanguageInput")!;
            var label = settings.FindControl<TextBlock>("CurrentLanguageLabel")!;
            Assert.Equal("auto", input.SelectedValue);
            Assert.Equal(4, input.ItemCount);
            Assert.True(input.IsEffectivelyVisible);
            input.Focus();
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.LanguageChangeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Layout();
            Assert.Equal("en-US", vm.SelectedLanguageCode);
            Assert.Equal("Current language: English", label.Text);
            Assert.DoesNotContain("切换语言失败", vm.StatusMessage);
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Language");
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            Assert.Equal("Settings", ((FANavigationViewItem)navigation.MenuItems[4]!).Content);
            Assert.Equal("en-US", new SettingsService(scope.Root).GetSetting(SettingKeys.Language, "auto"));
            Assert.Equal("外观", vm.SelectedCategory);
            Assert.Equal("Dark", vm.Theme);
            Assert.Equal(DeploymentMethod.Copy, vm.DefaultDeploymentMethod);

            scope.Services.GetRequiredService<NotificationService>().ShowSuccess("设置已保存", "设置");
            main.OpenNotificationsCommand.Execute(null);
            Layout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Settings saved");
            main.CloseOverlaysCommand.Execute(null);

            foreach (var code in new[] { "en-US", "zh-TW", "zh-CN" })
            {
                input.SelectedValue = code;
                await vm.LanguageChangeTask.WaitAsync(TimeSpan.FromSeconds(10));
                Layout();
                Assert.Equal(code, scope.Services.GetRequiredService<ILocalizationService>().CurrentLanguage);
                Assert.Equal(code, input.SelectedValue);
                Assert.Equal("外观", vm.SelectedCategory);
                Assert.DoesNotContain("切换语言失败", vm.StatusMessage);
                foreach (var size in new[] { (Width: 960d, Height: 600d), (Width: 1280d, Height: 800d) })
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    app.RequestedThemeVariant = theme;
                    Layout();
                    Assert.True(input.Bounds.Width >= 200);
                    Assert.True(input.Bounds.Height >= 28);
                    Assert.True(label.Bounds.Height > 0);
                    Capture(window, theme.ToString(), $"language-{code}-{size.Width:0}");
                }
            }
            Assert.Equal("当前语言：简体中文", label.Text);
            main.OpenNotificationsCommand.Execute(null);
            Layout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "设置已保存");
            main.CloseOverlaysCommand.Execute(null);
            main.NavigateToLibraryCommand.Execute(null);
            Layout();
            await scope.Services.GetRequiredService<ILocalizationService>().SetLanguageAsync("en-US");
            Layout();
            var library = window.GetVisualDescendants().OfType<LibraryView>().Single();
            Assert.Contains(library.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "HD2Mimnt Test Fluent 1");
            Assert.Contains(library.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Refresh library");
        }
        finally { window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task AutomaticLanguage_ShouldDispatchRefreshAndFollowResetAndReload()
    {
        var system = new TestSystemLanguage();
        await using var scope = await UiScope.CreateAsync(systemLanguage: system);
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var service = scope.Services.GetRequiredService<ILocalizationService>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var localizer = scope.Services.GetRequiredService<UiLocalization>();
        var window = new Window { Width = 1000, Height = 700, Content = new SettingsView { DataContext = vm } };
        try
        {
            vm.SelectedCategory = "外观";
            window.Show();
            var changedOnUi = false;
            System.ComponentModel.PropertyChangedEventHandler onChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(UiLocalization.Revision)) changedOnUi = Dispatcher.UIThread.CheckAccess();
            };
            localizer.PropertyChanged += onChanged;
            try
            {
                system.Culture = CultureInfo.GetCultureInfo("zh-HK");
                await Task.Run(service.RefreshSystemLanguage);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(changedOnUi);
                Assert.Equal("auto", vm.SelectedLanguageCode);
                Assert.Equal("目前語言：繁體中文", vm.CurrentLanguageText);

                vm.SelectedLanguageCode = "en-US";
                await vm.LanguageChangeTask;
                system.Culture = CultureInfo.GetCultureInfo("zh-CN");
                await Task.Run(service.RefreshSystemLanguage);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("en-US", service.CurrentLanguage);
                await settings.ResetCategoryAsync("UI");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("auto", vm.SelectedLanguageCode);
                Assert.Equal("zh-CN", service.CurrentLanguage);
                var external = new SettingsService(scope.Root);
                await external.SetSettingAsync(SettingKeys.Language, "zh-TW");
                await settings.ReloadAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("zh-TW", vm.SelectedLanguageCode);
            }
            finally { localizer.PropertyChanged -= onChanged; }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LanguageSelection_ShouldSerializeRapidChangesAndRollBackFailedSave()
    {
        await using var scope = await UiScope.CreateAsync(systemLanguage: new TestSystemLanguage());
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var service = scope.Services.GetRequiredService<ILocalizationService>();
        vm.SelectedLanguageCode = "en-US";
        vm.SelectedLanguageCode = "zh-TW";
        vm.SelectedLanguageCode = "zh-CN";
        await vm.LanguageChangeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsChangingLanguage);
        Assert.Equal("zh-CN", service.SelectedLanguage);
        Assert.Equal("zh-CN", new SettingsService(scope.Root).GetSetting(SettingKeys.Language, "auto"));
        using (var lockedFile = new FileStream(Path.Combine(scope.Root, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            vm.SelectedLanguageCode = "en-US";
            await vm.LanguageChangeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(vm.IsChangingLanguage);
            Assert.Equal("zh-CN", vm.SelectedLanguageCode);
            Assert.Equal("zh-CN", service.CurrentLanguage);
            Assert.StartsWith("切换语言失败：", vm.StatusMessage);
        }
        vm.SelectedLanguageCode = "en-US";
        await vm.LanguageChangeTask;
        Assert.Equal("en-US", service.CurrentLanguage);
    }

    [AvaloniaFact]
    public async Task LanguagePresentation_ShouldRefreshOpenDialogMenuAndPermissionWarning()
    {
        await using var scope = await UiScope.CreateAsync(systemLanguage: new TestSystemLanguage());
        var service = scope.Services.GetRequiredService<ILocalizationService>();
        var localizer = scope.Services.GetRequiredService<UiLocalization>();
        var target = new Button { Content = "Menu" };
        var window = new Window { Width = 700, Height = 600, Content = target };
        var permissionVm = new PluginPermissionDialogViewModel();
        permissionVm.Initialize("保存", "1.0", "设置", "用户描述", "FullAccess", new[] { "AccessNetwork" });
        var permissions = new PluginPermissionDialog { DataContext = permissionVm };
        try
        {
            window.Show();
            AppContextMenu.Show(new object[] { new ContextMenuItem("删除模组", new RelayCommand(() => { })) }, target);
            var menu = Assert.IsType<FAMenuFlyout>(target.ContextFlyout);
            var item = Assert.IsType<FAMenuFlyoutItem>(menu.Items[0]);
            Assert.Equal("删除模组", item.Text);
            await service.SetLanguageAsync("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Delete mod", item.Text);
            menu.Hide();
            var confirmation = new ConfirmationService();
            confirmation.SetOwner(window);
            var pending = confirmation.ConfirmAsync("删除配置文件", "确定删除配置文件“保存”吗？模组库中的模组不会被删除。");
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<FAContentDialog>());
            Assert.Equal("Cancel", dialog.CloseButtonText);
            Assert.Contains("保存", Assert.IsType<TextBlock>(dialog.Content).Text);
            await service.SetLanguageAsync("zh-TW");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("取消", dialog.CloseButtonText);
            Assert.Equal("刪除設定檔", dialog.Title);
            dialog.Hide();
            Assert.False(await pending);
            permissions.Show();
            await service.SetLanguageAsync("en-US");
            Dispatcher.UIThread.RunJobs(); permissions.UpdateLayout();
            var warning = Assert.Single(permissions.GetVisualDescendants().OfType<FAInfoBar>());
            Assert.DoesNotContain("此插件", warning.Message);
            Assert.Contains(permissions.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "保存");
            Assert.Contains(permissions.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "用户描述");
            Assert.Equal("diagnostic: 未知 0x123", localizer.Translate("diagnostic: 未知 0x123"));
            Assert.Contains("保存", localizer.Translate("与以下模组存在覆盖冲突：\n保存\n\n详细信息请查看“冲突”页。"));
            Assert.Equal("与以下模组存在覆盖冲突：\n{0}\n\n详细信息请查看“冲突”页。", new HD2Mimnt.Application.Localization.TranslationCatalog()
                .GetStrings(CultureInfo.GetCultureInfo("zh-CN"))["Profile.ConflictTooltip"].Replace("\r\n", "\n"));
            Assert.StartsWith("Overlaps with these mods:", localizer.Translate("与以下模组存在覆盖冲突：\n保存\n\n详细信息请查看“冲突”页。"));
            var conditional = new LocalizedTextConverter(localizer);
            Assert.Equal("保存", conditional.Convert(new object?[] { "保存", localizer.Revision, false }, typeof(string), null, localizer.Culture));
            Assert.Equal("Save", conditional.Convert(new object?[] { "保存", localizer.Revision, true }, typeof(string), null, localizer.Culture));
            Assert.Equal("Hard link", localizer.Translate(DeploymentMethod.HardLink));
            var platform = new ModPlatformLink("nexusmods", "helldivers2", "42", "https://www.nexusmods.com/helldivers2/mods/42");
            Assert.Contains("Unknown", localizer.Translate(platform));
            Assert.Contains("保存", localizer.Translate(platform with { Name = "保存" }));
            Assert.Equal("Bones (bones) · 0x18DEAD01056B72E9", localizer.TranslateResourceNames("骨骼 (bones) · 0x18DEAD01056B72E9"));
            var deployment = localizer.Translate("配置：设置\n启用模组：2 个\n部署方式：硬链接\n部署位置：C:\\保存\n将清理之前的部署并按配置顺序部署，是否继续？");
            Assert.Contains("Hard link", deployment, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("C:\\保存", deployment);
            Assert.Equal("Checking deployment compatibility: 保存", localizer.Translate("检查部署兼容性：保存"));
            var oldDate = DateTime.UtcNow.AddDays(-14);
            var dateConverter = new LocalizedTextConverter(localizer, innerConverter: new HD2Mimnt.UI.Converters.TimeAgoConverter());
            Assert.Equal(oldDate.ToLocalTime().ToString("MMM dd", localizer.Culture), dateConverter.Convert(
                new object?[] { oldDate, localizer.Revision }, typeof(string), null, localizer.Culture));
            await service.SetLanguageAsync("zh-TW");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(oldDate.ToLocalTime().ToString("MMM dd", localizer.Culture), dateConverter.Convert(
                new object?[] { oldDate, localizer.Revision }, typeof(string), null, localizer.Culture));
        }
        finally { permissions.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task TranslatedPages_ShouldRenderAtMinimumSizeAndKeepUserGroupNames()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true, systemLanguage: new TestSystemLanguage());
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profile = (await profiles.GetActiveProfileAsync())!;
        const string userGroup = "依赖加载器的模组与 BSL / MDL";
        await profiles.AddSeparatorAsync(profile.Id, userGroup, 0);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var service = scope.Services.GetRequiredService<ILocalizationService>();
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            foreach (var code in new[] { "en-US", "zh-TW" })
            {
                await service.SetLanguageAsync(code);
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme;
                    for (var page = 0; page < 5; page++)
                    {
                        main.SelectedTabIndex = page;
                        Layout();
                        Assert.Equal(page, window.FindControl<TabControl>("PageHost")!.SelectedIndex);
                        if (page == 0)
                        {
                            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == userGroup);
                            var status = window.GetVisualDescendants().OfType<ProfileView>().Single()
                                .FindControl<TextBlock>("ProfileConflictStatus")!;
                            Assert.DoesNotContain("配置或模组", status.Text);
                            var profileList = window.GetVisualDescendants().OfType<ProfileView>().Single().FindControl<ListBox>("ProfileList")!;
                            Assert.True(profileList.Bounds.Height > 60, $"Profile list bounds: {profileList.Bounds}");
                        }
                        if (page == 4)
                        {
                            foreach (var category in main.SettingsViewModel.SettingsCategories)
                            {
                                main.SettingsViewModel.SelectedCategory = category;
                                Layout();
                                Assert.Contains(window.GetVisualDescendants().OfType<FASettingsExpander>(), expander => expander.IsEffectivelyVisible);
                            }
                            main.SettingsViewModel.SelectedCategory = "游戏";
                            Layout();
                        }
                        Capture(window, theme.ToString(), $"translated-{code}-page-{page}");
                    }
                    main.SelectedTabIndex = 3;
                    foreach (var category in main.MoreViewModel.Categories)
                    {
                        main.MoreViewModel.SelectedCategory = category;
                        Layout();
                        Capture(window, theme.ToString(), $"translated-{code}-tools-{Array.IndexOf(main.MoreViewModel.Categories, category)}");
                    }
                }
            }
        }
        finally { window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    private sealed class TestSystemLanguage : ISystemLanguageProvider
    {
        public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("zh-CN");
        public CultureInfo GetCurrentCulture() => Culture;
    }
}
