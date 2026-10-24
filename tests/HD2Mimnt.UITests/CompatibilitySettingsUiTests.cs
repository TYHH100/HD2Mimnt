using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task CompatibilitySettings_StartupRefreshesStaleDataAndScanResultsStayReadOnlyUntilOpened()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(), compatibilityNames: new OfflineCompatibilityNames());
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var game = System.IO.Path.Combine(scope.Root, "game");
        WriteGame(game, new Resource(1, UnitType, Unit()));
        await settings.SetSettingAsync(SettingKeys.GamePath, game);
        var service = scope.Services.GetRequiredService<ModCompatibilityService>();
        await service.BuildGameDataAsync();
        WriteGame(game, new Resource(1, UnitType, Unit()), new Resource(2, UnitType, Unit()));
        await ImportCompatibilityMod(scope, "HD2Mimnt Test startup");
        await settings.SetSettingAsync(SettingKeys.CompatibilityCheckOnStartup, true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var compatibility = main.Compatibility!;
        try
        {
            await main.InitializeAsync(); await main.StartupCompatibilityTask;
            Assert.False(compatibility.IsOpen); Assert.False(compatibility.IsBusy);
            Assert.NotNull(Assert.Single(compatibility.Mods).Report);
            Assert.Equal(2, (await service.GetGameDataStatusAsync()).UnitCount);
            var notification = scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications().Single(n => n.ActionText == UiLocalization.Current["Settings.Compatibility.ShowResults"]);
            notification.Action!(); Dispatcher.UIThread.RunJobs(); Assert.True(compatibility.IsOpen);
            compatibility.RepairUnits = false; await compatibility.PreviewScopeCommand.ExecuteAsync(null);
            Assert.True(compatibility.CanRepairSelected); Assert.False(compatibility.CanRepairScope);
            await compatibility.RepairScopeCommand.ExecuteAsync(null);
            Assert.Empty(compatibility.Backups); Assert.True(compatibility.CanRepairSelected);
            await settings.SetSettingAsync(SettingKeys.CompatibilityEnableBatchRepair, true);
            Assert.True(compatibility.CanRepairScope);
            await compatibility.RepairScopeCommand.ExecuteAsync(null);
            Assert.NotEmpty(compatibility.Backups); Assert.False(compatibility.CanRepairSelected);
        }
        finally { await compatibility.StopAsync(); await main.SettingsViewModel.StopCompatibilityDataAsync(); }
    }

    [AvaloniaFact]
    public async Task CompatibilitySettings_ActualCategoryTogglesPersistResetAndBuildReusableGameData()
    {
        await using var scope = await UiScope.CreateAsync(compatibilityNames: new OfflineCompatibilityNames());
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var game = System.IO.Path.Combine(scope.Root, "game");
        WriteGame(game, new Resource(1, UnitType, Unit()));
        await settings.SetSettingAsync(SettingKeys.GamePath, game);
        var vm = scope.Services.GetRequiredService<SettingsViewModel>(); await vm.LoadAsync();
        var compatibility = scope.Services.GetRequiredService<ModCompatibilityViewModel>();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1040, Height = 800, Content = view };
        var app = Avalonia.Application.Current!; var original = app.RequestedThemeVariant;
        var language = scope.Services.GetRequiredService<ILocalizationService>(); UiLocalization.Current.Attach(language);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var category = view.GetVisualDescendants().OfType<ListBoxItem>().Single(c => c.DataContext as string == "兼容性");
            ClickVersionControl(window, category); await vm.CompatibilityDataTask;
            Assert.True(vm.IsCompatibilityCategoryVisible);
            Assert.True(view.FindControl<FASettingsExpander>("CompatibilitySettings")!.IsEffectivelyVisible);
            Assert.False(vm.CompatibilityCheckOnStartup); Assert.False(vm.CompatibilityEnableBatchRepair);
            ClickVersionControl(window, view.FindControl<ToggleSwitch>("CompatibilityStartupToggle")!);
            ClickVersionControl(window, view.FindControl<ToggleSwitch>("CompatibilityBatchToggle")!);
            Assert.True(vm.CompatibilityCheckOnStartup); Assert.True(vm.CompatibilityEnableBatchRepair);
            Assert.False(compatibility.EnableBatchRepair);
            ClickVersionControl(window, view.FindControl<Button>("SaveSettingsButton")!); await vm.SaveCommand.ExecutionTask!;
            Assert.True(compatibility.EnableBatchRepair);
            var saved = new SettingsService(scope.Root);
            Assert.True(saved.GetSetting(SettingKeys.CompatibilityCheckOnStartup, false));
            Assert.True(saved.GetSetting(SettingKeys.CompatibilityEnableBatchRepair, false));
            var build = view.FindControl<Button>("BuildCompatibilityDataButton")!; build.BringIntoView();
            ClickVersionControl(window, build); await vm.BuildCompatibilityDataCommand.ExecutionTask!;
            var service = scope.Services.GetRequiredService<ModCompatibilityService>();
            var status = await service.GetGameDataStatusAsync();
            Assert.Equal(GameReferenceCacheState.Ready, status.State); Assert.True(System.IO.File.Exists(status.CachePath));
            Assert.Equal(1, status.UnitCount);
            var reopened = new HD2Mimnt.Infrastructure.Analysis.GameUnitReferenceReader(scope.Services.GetRequiredService<StorageSettingsService>().Current.CachePath);
            Assert.True((await reopened.ReadAsync(game, [1])).IndexCacheHit);
            WriteGame(game, new Resource(2, UnitType, Unit()));
            Assert.Equal(GameReferenceCacheState.Stale, (await service.GetGameDataStatusAsync()).State);
            await vm.UpdateStaleCompatibilityDataAsync();
            Assert.Equal(GameReferenceCacheState.Ready, (await service.GetGameDataStatusAsync()).State);
            Assert.Equal(GameUnitReferenceStatus.Found, (await reopened.ReadAsync(game, [2])).Units[2].Status);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                app.RequestedThemeVariant = theme; await language.SetLanguageAsync(code);
                window.Width = 960; window.Height = 600;
                view.GetVisualDescendants().OfType<ScrollViewer>().Last(s => s.IsEffectivelyVisible).Offset = default;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame();
                Assert.Equal(language["Settings.Compatibility.Title"], view.FindControl<FASettingsExpander>("CompatibilitySettings")!.Header);
                Assert.Contains(language["Settings.Compatibility.Data.State.Ready"], view.FindControl<TextBlock>("CompatibilityDataStatus")!.Text);
                Assert.Equal(language["UI.On"], view.FindControl<ToggleSwitch>("CompatibilityStartupToggle")!.OnContent);
                var save = view.FindControl<Button>("SaveSettingsButton")!;
                var bottom = save.TranslatePoint(new Point(0, save.Bounds.Height), window)!.Value;
                Assert.InRange(bottom.Y, 0, window.Bounds.Height);
                if (theme == ThemeVariant.Dark && code == "zh-CN" && Environment.GetEnvironmentVariable("HD2MIMNT_UI_SCREENSHOT") is { } path) frame?.Save(path);
            }
            await vm.ResetCommand.ExecuteAsync(null);
            Assert.False(compatibility.EnableBatchRepair); Assert.False(vm.CompatibilityCheckOnStartup);
            Assert.Equal(game, settings.GetSetting(SettingKeys.GamePath, string.Empty));
            Assert.True(System.IO.File.Exists(status.CachePath));
            vm.SelectedCategory = "入口自定义";
            vm.SettingsEntries.Single(e => e.Id == "settings.compatibility").IsVisible = false;
            await vm.SaveCommand.ExecuteAsync(null); Assert.DoesNotContain("兼容性", vm.SettingsCategories);
            await vm.ResetCommand.ExecuteAsync(null); Assert.Contains("兼容性", vm.SettingsCategories);
        }
        finally { await vm.StopCompatibilityDataAsync(); await compatibility.StopAsync(); app.RequestedThemeVariant = original; window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompatibilitySettings_DisablingStartupCancelsAndWaitsForBackgroundCleanup()
    {
        var analyzer = new BlockingCompatibilityAnalyzer();
        await using var scope = await UiScope.CreateAsync(seedMods: true, compatibilityNames: new OfflineCompatibilityNames(), compatibilityAnalyzer: analyzer);
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var library = scope.Services.GetRequiredService<LibraryViewModel>(); await library.InitializeAsync();
        var vm = library.Compatibility!;
        await library.CheckCompatibilityOnStartupAsync(); Assert.False(analyzer.Started.Task.IsCompleted);
        await settings.SetSettingAsync(SettingKeys.CompatibilityCheckOnStartup, true);
        var scan = library.CheckCompatibilityOnStartupAsync();
        await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsOpen); Assert.True(vm.IsBusy);
        await settings.SetSettingAsync(SettingKeys.CompatibilityCheckOnStartup, false);
        await analyzer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = vm.StopAsync(); Assert.False(closing.IsCompleted);
        var input = new TextBox(); var window = new Window { Content = input };
        try { window.Show(); input.Focus(); window.KeyTextInput("responsive"); Assert.Equal("responsive", input.Text); }
        finally { analyzer.ReleaseCleanup.TrySetResult(); window.Close(); }
        await closing; await scan; Assert.True(analyzer.Cleaned); Assert.False(vm.IsBusy);
    }
}
