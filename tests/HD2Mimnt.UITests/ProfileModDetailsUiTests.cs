using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ProfileModContextMenu_ShouldConfirmAndDeleteLibraryFilesAcrossProfiles()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(seedMods: true, confirmation: confirmation);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var mod = vm.ProfileMods.First(row => !row.IsSeparator);
        var mods = scope.Services.GetRequiredService<ModService>();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var libraryMod = (await mods.GetModByIdAsync(mod.ModId))!;
        var otherProfileId = await profiles.CreateProfileAsync("同一模组的另一个配置");
        var activeProfileId = vm.SelectedProfile!.Id;
        var originalIds = vm.ProfileMods.Select(row => row.ModId).ToArray();
        foreach (var id in originalIds) await profiles.AddModToProfileAsync(otherProfileId, id);
        var groupIds = new Dictionary<Guid, Guid>();
        foreach (var profileId in new[] { activeProfileId, otherProfileId })
        {
            var separator = await profiles.AddSeparatorAsync(profileId, "删除后保持原成员", 1);
            await profiles.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
            groupIds.Add(profileId, separator.Id);
        }
        await vm.RefreshAfterProfilesChangedAsync();
        mod = vm.ProfileMods.Single(row => row.ModId == mod.ModId);
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 950 };
        try
        {
            window.Show();
            void Layout()
            { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            foreach (var confirmed in new[] { false, true })
            {
                main.NotificationPanelViewModel.DismissAllToasts();
                view.FindControl<ItemsControl>("ModItemsControl")!.ScrollIntoView(mod); Layout();
                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
                var point = card.TranslatePoint(new Point(100, card.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Layout();
                var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
                Assert.True(menu.IsOpen);
                var delete = menu.Items.OfType<FAMenuFlyoutItem>().Single(item => item.Text == "删除模组文件");
                Assert.Same(vm.DeleteModCommand, delete.Command);
                Assert.Same(mod, delete.CommandParameter);
                var pending = new TaskCompletionSource<bool>();
                confirmation.Pending = pending;
                var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(delete));
                popup.UpdateLayout();
                var menuPoint = delete.TranslatePoint(new Point(20, delete.Bounds.Height / 2), popup)!.Value;
                popup.MouseMove(menuPoint); popup.MouseDown(menuPoint, MouseButton.Left); popup.MouseUp(menuPoint, MouseButton.Left); Layout();
                Assert.True(confirmation.PendingConfirmation);
                Assert.Contains(mod.ModName, confirmation.Message);
                Assert.Contains("此操作会删除模组文件", confirmation.Message);
                Assert.True(Directory.Exists(libraryMod.Path));
                Assert.NotNull(await mods.GetModByIdAsync(mod.ModId));
                pending.SetResult(confirmed);
                await vm.DeleteModCommand.ExecutionTask!;
                if (!confirmed)
                {
                    Assert.True(Directory.Exists(libraryMod.Path));
                    Assert.NotNull(await mods.GetModByIdAsync(mod.ModId));
                    Assert.Contains(vm.ProfileMods, row => row.ModId == mod.ModId);
                }
            }
            Assert.False(Directory.Exists(libraryMod.Path));
            Assert.Null(await mods.GetModByIdAsync(mod.ModId));
            for (var attempt = 0; attempt < 100 && vm.ProfileMods.Any(row => row.ModId == mod.ModId); attempt++)
                await Task.Delay(20);
            Assert.DoesNotContain(vm.ProfileMods, row => row.ModId == mod.ModId);
            Assert.DoesNotContain((await profiles.GetProfileByIdAsync(otherProfileId))!.Mods, row => row.ModId == mod.ModId);
            var group = Assert.Single(vm.ProfileSeparators);
            Assert.Equal(0, group.Order);
            Assert.True(group.IsCollapsed);
            Assert.Equal(originalIds.Skip(1), vm.GetSeparatorMembers(group).Select(row => row.ModId));
            Assert.Empty(vm.VisibleProfileMods);
            foreach (var profileId in groupIds.Keys)
            {
                var saved = (await profiles.GetProfileByIdAsync(profileId))!;
                var savedGroup = Assert.Single(saved.Separators);
                Assert.Equal(groupIds[profileId], savedGroup.Id);
                Assert.Equal(0, savedGroup.Order);
                Assert.Equal(originalIds.Length - 1, savedGroup.MemberCount);
                Assert.True(savedGroup.IsCollapsed);
            }
            for (var attempt = 0; attempt < 100 && main.LibraryViewModel.FilteredMods.Any(row => row.Id == mod.ModId); attempt++)
                await Task.Delay(20);
            Assert.DoesNotContain(main.LibraryViewModel.FilteredMods, row => row.Id == mod.ModId);
        }
        finally { await vm.CloseModDetailAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileModDetails_ShouldSupportDoubleClickAndContextMenusWithLoadersLocked(bool bottomToTop)
    {
        await using var scope = await UiScope.CreateAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var repository = scope.Services.GetRequiredService<IModRepository>();
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
            (bottomToTop ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom).ToString());
        var profileId = await profiles.CreateProfileAsync("详情与固定加载器");
        await profiles.SetActiveProfileAsync(profileId);
        foreach (var role in new[] { BslModRole.None, BslModRole.Dependency, BslModRole.Loader, BslModRole.MdlLoader, BslModRole.SmoothBoot })
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(), Name = $"HD2Mimnt Test Details {role}", Version = "1.2.3", Author = "Detail Author",
                Path = scope.Root, Hash = "known-hash", BslRole = role, BslDetectionVersion = BslModDetector.DetectionVersion
            };
            await repository.AddAsync(mod);
            await scope.Services.GetRequiredService<ModService>().SetPlatformLinkAsync(mod.Id,
                new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633",
                    AuthorUrl: "https://afdian.com/a/example"));
            await profiles.AddModToProfileAsync(profileId, mod.Id);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 950 };
        try
        {
            window.Show();
            void Layout()
            { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control, MouseButton button = MouseButton.Left)
            {
                control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(12, control.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, button); window.MouseUp(point, button); Layout();
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            foreach (var mod in vm.ProfileMods.ToArray())
            {
                view.FindControl<ItemsControl>("ModItemsControl")!.ScrollIntoView(mod); Layout();
                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
                var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == mod.ModName);
                Click(title);
                Assert.False(vm.IsDetailPanelOpen);
                Click(title);
                Assert.True(vm.IsDetailPanelOpen);
                Assert.Equal(0, main.SelectedTabIndex);
                Assert.Same(mod.SourceInfo, vm.SelectedModDetail);
                Assert.Equal("1.2.3", vm.SelectedModDetail!.Version);
                Assert.Equal("Detail Author", vm.SelectedModDetail.Author);
                Assert.Equal(mod.Enabled, vm.SelectedModDetail.IsEnabled);
                Assert.True(view.GetVisualDescendants().OfType<ModDetailPanel>().Single().IsEffectivelyVisible);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
                Assert.False(vm.IsDetailPanelOpen);

                vm.SelectAllVisibleMods();
                var selected = vm.ProfileMods.Where(row => row.IsSelected).Select(row => row.ModId).ToArray();
                Click(title, MouseButton.Right);
                var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
                Assert.True(menu.IsOpen);
                Assert.Equal(selected, vm.ProfileMods.Where(row => row.IsSelected).Select(row => row.ModId));
                var items = menu.Items.OfType<FAMenuFlyoutItem>().ToArray();
                Assert.Contains(items, item => item.Text == "打开文件位置" && item.IsEffectivelyEnabled);
                Assert.Contains(items, item => item.Text == "打开来源网页" && item.IsEffectivelyEnabled);
                Assert.Contains(items, item => item.Text == "打开作者主页" && item.IsEffectivelyEnabled);
                Assert.Contains(items, item => item.Text == "删除模组文件" && item.IsEffectivelyEnabled
                    && ReferenceEquals(item.Command, vm.DeleteModCommand) && ReferenceEquals(item.CommandParameter, mod));
                Assert.Equal(mod.CanDrag, items.Any(item => item.Text == "移动到指定位置…"));
                Assert.Equal(!mod.IsFixedGroupMember, items.Any(item => item.Text == "在此处添加分组分隔符"));
                if (!mod.CanSelect) Assert.False(mod.IsSelected || mod.IsFocused);
                var details = items.Single(item => item.Text == "查看详情");
                var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(details));
                popup.UpdateLayout();
                var menuPoint = details.TranslatePoint(new Point(20, details.Bounds.Height / 2), popup)!.Value;
                popup.MouseMove(menuPoint); popup.MouseDown(menuPoint, MouseButton.Left); popup.MouseUp(menuPoint, MouseButton.Left); Layout();
                Assert.True(vm.IsDetailPanelOpen);
                Assert.Same(mod.SourceInfo, vm.SelectedModDetail);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
                vm.ClearModSelectionCommand.Execute(null);
            }
            Assert.Equal(profileId, (await profiles.GetActiveProfileAsync())!.Id);
            Assert.False(vm.ProfileMods.Single(mod => mod.IsSmoothBoot).CanDrag);
            Assert.All(vm.ProfileMods.Where(mod => mod.IsBslLoader), mod => Assert.False(mod.CanDrag));
        }
        finally { await vm.CloseModDetailAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileModDetails_ShouldChangeViewedProfileAndCloseOnProfileSwitch()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var active = (await profiles.GetActiveProfileAsync())!;
        var modId = active.Mods[0].ModId;
        var viewedId = await profiles.CreateProfileAsync("只查看的配置");
        await profiles.AddModToProfileAsync(viewedId, modId);
        var vm = main.ProfileViewModel;
        await vm.RefreshAfterProfilesChangedAsync();
        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Id == viewedId);
        await vm.RetryProfileModsCommand.ExecuteAsync(null);
        var mod = Assert.Single(vm.ProfileMods);
        vm.ShowModDetailCommand.Execute(mod);
        var detail = vm.SelectedModDetail!;
        var originalEnabled = mod.Enabled;
        detail.ToggleEnabledCommand!.Execute(null);
        for (var attempt = 0; attempt < 100 && detail.IsEnabled == originalEnabled; attempt++) await Task.Delay(20);
        Assert.Equal(!originalEnabled, detail.IsEnabled);
        Assert.Equal(!originalEnabled, (await profiles.GetProfileByIdAsync(viewedId))!.Mods.Single().Enabled);
        Assert.Equal(active.Mods.Single(row => row.ModId == modId).Enabled,
            (await profiles.GetProfileByIdAsync(active.Id))!.Mods.Single(row => row.ModId == modId).Enabled);
        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Id == active.Id);
        Assert.False(vm.IsDetailPanelOpen);
        Assert.False(detail.IsCalculatingHash);
        await vm.CloseModDetailAsync();
    }
}
