using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ProfileLocalChanges_ShouldKeepCardsAndDebounceAutomaticConflictChecks()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var conflicts = main.ConflictsViewModel;
        var cards = vm.ProfileMods.ToArray();
        var scans = 0;
        conflicts.ScanCompleted += (_, _) => scans++;
        conflicts.SetAutomaticChecking(true);
        try
        {
            var durations = new List<double>();
            for (var index = 0; index < 6; index++)
            {
                var clock = Stopwatch.StartNew();
                await vm.ToggleModEnabledCommand.ExecuteAsync(cards[0]);
                await main.WaitForRefreshesAsync();
                durations.Add(clock.Elapsed.TotalMilliseconds);
                Assert.Equal(0, scans);
                Assert.Equal(cards, vm.ProfileMods);
                await Task.Delay(250);
            }
            // 选项保存和真实排序也沿用同一合并窗口，且不重建已有卡片。
            await vm.PersistModOptionsAsync(cards[0]);
            await main.WaitForRefreshesAsync();
            Assert.Equal(cards, vm.ProfileMods);
            await vm.MoveModsAsync([cards[1].ModId], cards[0].ModId);
            await main.WaitForRefreshesAsync();
            Assert.Equal(cards[1].ModId, vm.ProfileMods[0].ModId);
            Assert.All(cards, card => Assert.Same(card, vm.ProfileMods.Single(row => row.ModId == card.ModId)));
            Assert.Equal(0, scans);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (scans == 0 || conflicts.IsScanning) await Task.Delay(20, timeout.Token);
            Assert.Equal(1, scans);
            await Task.Delay(1700);
            Assert.Equal(1, scans);
            Console.WriteLine($"连续开关和同步刷新耗时：最大 {durations.Max():F1} ms，平均 {durations.Average():F1} ms；扫描 {scans} 次");
        }
        finally { await conflicts.StopScanAsync(); }
    }

    [AvaloniaFact]
    public async Task ProfileContextMenu_ShouldAddModToOtherProfilesWithoutLeavingPage()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var destination = await profiles.CreateProfileAsync("目标配置");
        await main.WaitForRefreshesAsync();
        var sourceId = vm.SelectedProfile!.Id;
        var mod = vm.ProfileMods.First(row => !row.IsSeparator);
        var before = (await profiles.GetProfileByIdAsync(sourceId))!.Mods
            .Select(row => (row.ModId, row.Enabled, row.Order, row.OptionsJson)).ToArray();
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 950 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var card = view.GetVisualDescendants().OfType<Border>().Single(border =>
                border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
            var point = card.TranslatePoint(new Point(100, card.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Layout();
            var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            var add = menu.Items.OfType<FAMenuFlyoutItem>().Single(item => item.Text == "添加到其他配置文件…");
            Assert.Same(vm.AddModToOtherProfilesCommand, add.Command);
            var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(add));
            popup.UpdateLayout();
            var addPoint = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(addPoint, MouseButton.Left); popup.MouseUp(addPoint, MouseButton.Left);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!main.LibraryViewModel.IsAddToProfileDialogOpen) await Task.Delay(20, timeout.Token);
            Layout();
            Assert.Equal(0, main.SelectedTabIndex);
            Assert.DoesNotContain(main.LibraryViewModel.AddToProfileChoices, choice => choice.Id == sourceId);
            var overlay = view.GetVisualDescendants().OfType<AddToProfileOverlay>().Single();
            Assert.True(overlay.FindControl<Border>("AddToProfileDialogSurface")!.IsEffectivelyVisible);
            await scope.Services.GetRequiredService<LocalizationService>().SetLanguageAsync("en-US");
            Layout();
            Assert.Equal("Add to other profiles…", HD2Mimnt.UI.Localization.UiLocalization.Current["Profile.AddToOtherProfiles"]);
            await scope.Services.GetRequiredService<LocalizationService>().SetLanguageAsync("zh-CN");
            foreach (var choice in main.LibraryViewModel.AddToProfileChoices) choice.IsSelected = choice.Id == destination;
            await main.LibraryViewModel.ConfirmAddToProfileCommand.ExecuteAsync(null);
            await main.WaitForRefreshesAsync();
            Assert.False(main.LibraryViewModel.IsAddToProfileDialogOpen);
            Assert.True((await profiles.GetProfileByIdAsync(destination))!.Mods.Single(row => row.ModId == mod.ModId).Enabled);
            Assert.Equal(before, (await profiles.GetProfileByIdAsync(sourceId))!.Mods
                .Select(row => (row.ModId, row.Enabled, row.Order, row.OptionsJson)).ToArray());
            Assert.Equal(sourceId, vm.SelectedProfile!.Id);
            await vm.AddModToOtherProfilesCommand.ExecuteAsync(vm.ProfileMods.First(row => !row.IsSeparator));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(main.LibraryViewModel.IsAddToProfileDialogOpen);
        }
        finally { window.Close(); }
    }
}
