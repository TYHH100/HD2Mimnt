using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task CollapsedSeparatorDrop_ShouldLeaveModsVisibleBelowInBothDirections()
    {
        foreach (var bottomToTop in new[] { false, true })
        {
            await using var scope = await UiScope.CreateAsync(seedMods: true);
            var profiles = scope.Services.GetRequiredService<ProfileService>();
            var profileId = (await profiles.GetActiveProfileAsync())!.Id;
            var separator = await profiles.AddSeparatorAsync(profileId, "收起分组", 1);
            await profiles.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
            await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
                bottomToTop ? "BottomToTop" : "TopToBottom");
            var main = scope.Services.GetRequiredService<HD2Mimnt.UI.ViewModels.MainWindowViewModel>();
            await main.InitializeAsync();
            var vm = main.ProfileViewModel;
            var moving = Assert.Single(vm.VisibleProfileMods);
            var group = Assert.Single(vm.ProfileSeparators);
            var members = vm.GetSeparatorMembers(group).Select(row => row.ModId).ToArray();
            var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
            try
            {
                window.Show();
                void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                Layout();
                var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
                var list = view.FindControl<ItemsControl>("ModItemsControl")!;
                var container = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(vm.ProfileRows.IndexOf(group)));
                var point = container.TranslatePoint(new Point(8, container.Bounds.Height - 1), list)!.Value;
                var data = new DataTransfer();
                var movingIds = new[] { moving.ModId };
                data.Add(DataTransferItem.CreateText(moving.ModId.ToString()));
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, list, point, KeyModifiers.None);
                list.RaiseEvent(over);
                Assert.Equal(DragDropEffects.Move, over.DragEffects);
                Assert.True(view.FindControl<Border>("SortInsertionMarker")!.IsVisible);
                var (_, anchor) = ((int, Guid?))InvokeSortPreview(view, "GetModDropTarget", movingIds, point.Y)!;
                Assert.Equal(bottomToTop ? (Guid?)null : Guid.Empty, anchor);
                list.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, list, point, KeyModifiers.None));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while ((await profiles.GetProfileByIdAsync(profileId))!.Mods.OrderBy(row => row.Order).Last().ModId != moving.ModId)
                    await Task.Delay(10, timeout.Token);
                Layout();
                Assert.False(view.FindControl<Border>("SortInsertionMarker")!.IsVisible);
                Assert.Equal(moving.ModId, Assert.Single(vm.VisibleProfileMods).ModId);
                Assert.Equal(members, vm.GetSeparatorMembers(group).Select(row => row.ModId));
                Assert.True(vm.ProfileRows.IndexOf(moving) > vm.ProfileRows.IndexOf(group));
                Assert.True(list.ContainerFromIndex(vm.ProfileRows.IndexOf(moving))!.IsEffectivelyVisible);
                await vm.LoadProfilesCommand.ExecuteAsync(null);
                Assert.Equal(moving.ModId, Assert.Single(vm.VisibleProfileMods).ModId);
                group = Assert.Single(vm.ProfileSeparators);
                Assert.Equal(2, group.MemberCount);
                await vm.ToggleSeparatorCommand.ExecuteAsync(group);
                Assert.Equal(members, vm.GetSeparatorMembers(group).Select(row => row.ModId));
                Assert.Equal(3, vm.VisibleProfileMods.Count);
            }
            finally { window.DataContext = null; window.Close(); }
        }
    }
}
