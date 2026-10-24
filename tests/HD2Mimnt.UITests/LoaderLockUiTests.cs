using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoaderLock_ShouldDefaultOnUnlockActualDragAndNumericMoveThenRelock(bool bottomToTop)
    {
        await using var scope = await UiScope.CreateAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
            (bottomToTop ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom).ToString());
        var id = await profiles.CreateProfileAsync("加载器锁定");
        await profiles.SetActiveProfileAsync(id);
        foreach (var role in new[] { BslModRole.None, BslModRole.Dependency, BslModRole.Loader, BslModRole.SmoothBoot })
        {
            var mod = new Mod { Id = Guid.NewGuid(), Name = $"HD2Mimnt Test Lock {role}", Path = scope.Root,
                BslRole = role, BslDetectionVersion = BslModDetector.DetectionVersion };
            await repository.AddAsync(mod);
            await profiles.AddModToProfileAsync(id, mod.Id);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var preferences = main.SettingsViewModel;
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 950 };
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
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            async Task WaitForLock(bool locked)
            {
                for (var attempt = 0; attempt < 250 && (vm.IsProfileModsLoading || vm.ProfileMods.Count != 4
                    || vm.ProfileMods.Any(mod => mod.LoaderLockEnabled != locked)); attempt++) await Task.Delay(20);
                Assert.Equal(4, vm.ProfileMods.Count);
                Assert.All(vm.ProfileMods, mod => Assert.Equal(locked, mod.LoaderLockEnabled));
                Layout();
            }
            await WaitForLock(true);
            Assert.True(preferences.LockLoaders);
            Assert.False(vm.ProfileMods.Single(mod => mod.IsBslLoader).CanDrag);
            main.NavigateToSettingsCommand.Execute(null);
            preferences.SelectedCategory = "配置文件"; Layout();
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var toggle = view.FindControl<ToggleSwitch>("LockLoadersToggle")!;
            Click(toggle);
            Assert.False(preferences.LockLoaders);
            Click(view.FindControl<Button>("SaveSettingsButton")!);
            await preferences.SaveCommand.ExecutionTask!;
            await WaitForLock(false);
            Assert.False(settings.GetSetting(SettingKeys.ProfilesLockLoaders, true));
            Assert.All(vm.ProfileMods, mod => Assert.True(mod.CanSelect && mod.CanDrag));
            Assert.DoesNotContain(vm.ProfileRows, row => row.IsSeparator && !row.IsUserSeparator);

            main.NavigateToProfilesCommand.Execute(null); Layout();
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var profileView = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var list = profileView.FindControl<ItemsControl>("ModItemsControl")!;
            var loader = vm.ProfileMods.Single(mod => mod.IsBslLoader);
            var source = (Control)list.ContainerFromIndex(vm.ProfileRows.IndexOf(loader))!;
            var target = (Control)list.ContainerFromIndex(0)!;
            var start = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window)!.Value;
            var end = target.TranslatePoint(new Point(target.Bounds.Width / 2, 8), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(0, 12), RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); Layout();
            Assert.True(profileView.FindControl<Border>("SortInsertionMarker")!.IsVisible);
            window.MouseUp(end, MouseButton.Left); Layout();
            for (var attempt = 0; attempt < 250 && (await profiles.GetProfileByIdAsync(id))!.Mods.First().ModId != loader.ModId; attempt++)
                await Task.Delay(20);
            Assert.Equal(loader.ModId, (await profiles.GetProfileByIdAsync(id))!.Mods.First().ModId);

            vm.OpenMoveToPositionCommand.Execute(loader);
            vm.MoveToPositionText = "4";
            await vm.ConfirmMoveToPositionCommand.ExecuteAsync(null);
            Assert.Empty(vm.MoveToPositionError);
            Assert.Equal(loader.ModId, (await profiles.GetProfileByIdAsync(id))!.Mods.Last().ModId);
            await vm.ToggleModEnabledCommand.ExecuteAsync(loader);
            Assert.False((await profiles.GetProfileByIdAsync(id))!.Mods.Single(mod => mod.ModId == loader.ModId).Enabled);

            main.NavigateToSettingsCommand.Execute(null); Layout();
            Click(toggle);
            await preferences.SaveCommand.ExecuteAsync(null);
            await WaitForLock(true);
            Assert.False(vm.ProfileMods.Single(mod => mod.IsBslLoader).CanDrag);
            Assert.Contains(vm.ProfileRows, row => row.IsSeparator && !row.IsUserSeparator);
            var modsById = (await repository.GetAllAsync()).ToDictionary(mod => mod.Id);
            Assert.Equal(new[] { BslModRole.None, BslModRole.Dependency, BslModRole.Loader, BslModRole.SmoothBoot },
                (await profiles.GetProfileByIdAsync(id))!.Mods.Select(mod => BslProfilePolicy.Role(modsById[mod.ModId])));
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
