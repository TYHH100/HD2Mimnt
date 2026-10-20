using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task MergedSettings_ActualTabsShowBothSectionsAndHonorIndividualVisibility()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 960, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var categories = view.FindControl<ListBox>("SettingsCategoryList")!;
            Assert.Equal(10, categories.ItemCount);
            foreach (var (category, first, second) in new[]
            {
                ("配置文件", "ProfileSettings", "DeploymentSettings"),
                ("模组库", "LibrarySettings", "SearchSettingsExpander"),
                ("存储位置", "StorageSettings", "CacheSettings")
            })
            {
                var item = categories.GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(item => item.DataContext as string == category);
                ClickVersionControl(window, item);
                Assert.Equal(category, vm.SelectedCategory);
                Assert.True(view.FindControl<FASettingsExpander>(first)!.IsEffectivelyVisible);
                Assert.True(view.FindControl<FASettingsExpander>(second)!.IsEffectivelyVisible);
            }
            var settings = scope.Services.GetRequiredService<SettingsService>();
            await settings.SetSettingAsync(SettingKeys.HiddenEntries, new[] { "settings.storage" });
            await vm.LoadAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("存储位置", categories.SelectedItem);
            Assert.False(view.FindControl<FASettingsExpander>("StorageSettings")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<FASettingsExpander>("CacheSettings")!.IsEffectivelyVisible);
            await settings.SetSettingAsync(SettingKeys.HiddenEntries, new[] { "settings.storage", "settings.cache" });
            await vm.LoadAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.DoesNotContain("存储位置", categories.Items.Cast<string>());
            Assert.Equal("入口自定义", categories.SelectedItem);
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
