using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task SearchSettings_ActualTogglesPersistResetAndLocalize()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await vm.LoadAsync();
        vm.SelectedCategory = "搜索";
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

            var expander = view.FindControl<FASettingsExpander>("SearchSettingsExpander")!;
            var fuzzy = view.FindControl<ToggleSwitch>("SearchFuzzyToggle")!;
            var pinyin = view.FindControl<ToggleSwitch>("SearchPinyinToggle")!;
            Assert.True(expander.IsEffectivelyVisible);
            Assert.False(vm.SearchFuzzy);
            Assert.False(vm.SearchPinyin);
            Assert.False(fuzzy.IsChecked);
            Assert.False(pinyin.IsChecked);
            Assert.Equal(language["Settings.Search.Title"], expander.Header);

            ClickVersionControl(window, fuzzy);
            Assert.True(vm.SearchFuzzy);
            ClickVersionControl(window, pinyin);
            Assert.True(vm.SearchPinyin);
            ClickVersionControl(window, view.FindControl<Button>("SaveSettingsButton")!);
            await vm.SaveCommand.ExecutionTask!;

            var saved = new SettingsService(scope.Root);
            Assert.True(saved.GetSetting(SettingKeys.SearchFuzzy, false));
            Assert.True(saved.GetSetting(SettingKeys.SearchPinyin, false));

            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await language.SetLanguageAsync(code);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(language["Settings.Search.Title"], expander.Header);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == language["Settings.Search.Fuzzy"]);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == language["Settings.Search.Pinyin"]);
                Assert.Equal(language["UI.On"], fuzzy.OnContent);
            }

            await vm.ResetCommand.ExecuteAsync(null);
            Assert.False(vm.SearchFuzzy);
            Assert.False(vm.SearchPinyin);
            var reset = new SettingsService(scope.Root);
            var resetValues = await reset.GetAllSettingsAsync();
            Assert.False(resetValues.ContainsKey(SettingKeys.SearchFuzzy));
            Assert.False(resetValues.ContainsKey(SettingKeys.SearchPinyin));
        }
        finally
        {
            app.RequestedThemeVariant = original;
            window.Close();
        }
    }
}
