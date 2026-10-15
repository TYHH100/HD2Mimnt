using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task EntryCustomization_ShouldHideRestoreAndKeepNavigationTagsAcrossThemesAndLanguages()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        main.NavigateToSettingsCommand.Execute(null);
        var vm = main.SettingsViewModel;
        vm.GamePath = "invalid-game-path";
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(16, control.Bounds.Height / 2), window)!.Value;
                Assert.Same(control, (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(item => ReferenceEquals(item, control)));
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var categories = settings.FindControl<ListBox>("SettingsCategoryList")!;
            Click(categories.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext as string == "入口自定义"));
            Assert.True(vm.IsEntriesCategoryVisible);
            var navigationOptions = settings.FindControl<ItemsControl>("NavigationEntryOptions")!;
            var browse = navigationOptions.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => toggle.DataContext is EntryVisibilityOption { Id: "navigation.browse" });
            Click(browse);
            Assert.False(vm.NavigationEntries.Single(entry => entry.Id == "navigation.browse").IsVisible);
            vm.SettingsEntries.Single(entry => entry.Id == "settings.cache").IsVisible = false;
            Click(settings.FindControl<Button>("SaveSettingsButton")!);
            await vm.SaveCommand.ExecutionTask!; Layout();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            var browseEntry = navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "5");
            Assert.False(browseEntry.IsVisible);
            Assert.DoesNotContain("缓存", categories.Items.Cast<string>());
            Assert.Equal("入口自定义", categories.SelectedItem);
            Assert.True(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "4").IsVisible);
            Assert.Contains("navigation.browse", scope.Services.GetRequiredService<SettingsService>().GetSetting(SettingKeys.HiddenEntries, Array.Empty<string>()));

            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language);
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme;
                    window.Width = 960; Layout();
                    main.NotificationPanelViewModel.DismissAllToasts(); Layout();
                    Assert.True(browse.IsEffectivelyVisible);
                    Assert.True(browse.Bounds.Width > 0);
                    Capture(window, theme.ToString(), $"entry-customization-{language}");
                }
            }

            await vm.ResetCommand.ExecuteAsync(null); Layout();
            Assert.True(browseEntry.IsVisible);
            Assert.Contains("缓存", categories.Items.Cast<string>());
            Click(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "1"));
            Assert.Equal(1, main.SelectedTabIndex);
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }
}
