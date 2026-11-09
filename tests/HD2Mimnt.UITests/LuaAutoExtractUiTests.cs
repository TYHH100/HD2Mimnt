using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

/// <summary>自动拆包 Lua 设置项的 Headless 回归：真实点击开关、持久化、三语与分类重置。</summary>
public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task LuaAutoExtract_ActualTogglePersistsResetsAndLocalizes()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        vm.SelectedCategory = "模组库";
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1040, Height = 800, Content = view };
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        UiLocalization.Current.Attach(language);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var library = view.FindControl<FASettingsExpander>("LibrarySettings")!;
            var toggle = view.FindControl<ToggleSwitch>("LuaAutoExtractToggle")!;
            Assert.True(library.IsEffectivelyVisible);
            // 默认关闭：加载与控件状态一致。
            Assert.False(vm.LuaAutoExtract);
            Assert.False(toggle.IsChecked);
            Assert.True(toggle.IsEffectivelyVisible);

            toggle.BringIntoView();
            ClickVersionControl(window, toggle);
            Assert.True(vm.LuaAutoExtract);
            ClickVersionControl(window, view.FindControl<Button>("SaveSettingsButton")!);
            await vm.SaveCommand.ExecutionTask!;

            var saved = new SettingsService(scope.Root);
            Assert.True(saved.GetSetting(SettingKeys.LuaAutoExtract, false));

            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await language.SetLanguageAsync(code);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == language["Settings.Library.LuaAutoExtract"]);
                Assert.Equal(language["UI.On"], toggle.OnContent);
            }

            // 分类重置覆盖组内设置：Lua 键随模组库前缀一并恢复默认并从库中移除。
            await vm.ResetCommand.ExecuteAsync(null);
            Assert.False(vm.LuaAutoExtract);
            var reset = new SettingsService(scope.Root);
            var resetValues = await reset.GetAllSettingsAsync();
            Assert.False(resetValues.ContainsKey(SettingKeys.LuaAutoExtract));
        }
        finally
        {
            app.RequestedThemeVariant = original;
            window.Close();
        }
    }
}
