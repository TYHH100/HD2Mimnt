using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public Task ProfileModSort_ActualPointerDrag_ShouldScrollBothWaysAndSaveDrop()
        => VerifySortDragScrollAsync(profileList: false);

    [AvaloniaFact]
    public Task ProfileListSort_ActualPointerDrag_ShouldScrollBothWaysAndSaveDrop()
        => VerifySortDragScrollAsync(profileList: true);

    private async Task VerifySortDragScrollAsync(bool profileList)
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        if (profileList)
        {
            for (var index = 0; index < 24; index++) await profiles.CreateProfileAsync($"滚轮配置 {index:00}");
        }
        else
        {
            var data = scope.Services.GetRequiredService<TestDataService>();
            await data.ImportGeneratedModsAsync(Enumerable.Range(1, 40).Select(index => new Mod
            {
                Name = $"HD2Mimnt Test Drag Scroll {index:00}", Author = "Helldivers", Version = "1.0.0",
                Path = Path.Combine(scope.Root, "drag-scroll", index.ToString())
            }));
            var profile = (await profiles.GetActiveProfileAsync())!;
            foreach (var mod in (await data.GetAllModsAsync()).Where(mod => !profile.Mods.Any(row => row.ModId == mod.Id)))
                await profiles.AddModToProfileAsync(profile.Id, mod.Id);
        }
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var list = view.FindControl<ItemsControl>(profileList ? "ProfileList" : "ModItemsControl")!;
            var viewport = profileList ? (Control)list : view.FindControl<ScrollViewer>("ModScrollViewer")!;
            var scroll = profileList ? list.GetVisualDescendants().OfType<ScrollViewer>().First() : (ScrollViewer)viewport;
            var movingId = profileList ? vm.Profiles[1].Id : vm.ProfileMods[0].ModId;
            var sourceIndex = profileList ? 1 : vm.ProfileRows.IndexOf(vm.ProfileMods[0]);
            var source = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(sourceIndex));
            var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window)!.Value;
            var center = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
            var marker = view.FindControl<Border>("SortInsertionMarker")!;

            void StartDrag()
            {
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(point + new Vector(0, 12), RawInputModifiers.LeftMouseButton);
                window.MouseMove(center, RawInputModifiers.LeftMouseButton);
                Layout();
                Assert.True(marker.IsVisible, "实际按下并移动后应进入排序预览");
            }
            StartDrag();
            for (var step = 0; step < 8; step++) { window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.LeftMouseButton); Layout(); }
            var downOffset = scroll.Offset.Y;
            Assert.True(downOffset > 0, "按住卡片滚轮向下应滚动列表");
            window.MouseWheel(center, new Vector(0, 3), RawInputModifiers.LeftMouseButton);
            Layout();
            Assert.True(scroll.Offset.Y < downOffset, "拖动期间滚轮向上也应生效");
            Assert.True(marker.IsVisible, "滚动后不移动鼠标也应刷新预览");
            foreach (var container in list.GetRealizedContainers())
            {
                var isMoving = profileList ? container.DataContext is ProfileItemViewModel row && row.Id == movingId
                    : container.DataContext is ProfileModViewModel mod && mod.ModId == movingId;
                Assert.Equal(isMoving ? 0.35 : 1, container.Opacity);
            }
            var y = window.TranslatePoint(center, list)!.Value.Y;
            var expected = profileList
                ? ((int Slot, int Target))InvokeSortPreview(view, "GetProfileDropTarget", movingId, y)!
                : default;
            Guid? anchor = null;
            if (!profileList)
            {
                var target = ((int Slot, Guid? Anchor))InvokeSortPreview(view, "GetModDropTarget", new[] { movingId }, y)!;
                anchor = target.Anchor;
            }
            Assert.False(view.FindControl<Border>("ImportDragHint")?.IsVisible ?? false);
            window.MouseUp(center, MouseButton.Left);
            Layout();
            Assert.False(marker.IsVisible);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (profileList ? vm.Profiles[expected.Target].Id != movingId
                : (await profiles.GetProfileByIdAsync(vm.SelectedProfile!.Id))!.Mods.OrderBy(row => row.Order)
                    .Select(row => row.ModId).ToList().IndexOf(movingId) <= 0)
                await Task.Delay(10, timeout.Token);
            Layout();
            if (!profileList && anchor is { } id && id != Guid.Empty)
            {
                var saved = (await profiles.GetProfileByIdAsync(vm.SelectedProfile!.Id))!.Mods.OrderBy(row => row.Order).Select(row => row.ModId).ToList();
                Assert.Equal(saved.IndexOf(id) - 1, saved.IndexOf(movingId));
            }

            // 取消后恢复源透明度与普通滚轮，不能遗留指针捕获。
            scroll.Offset = default;
            Layout();
            source = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(1));
            point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window)!.Value;
            var originalOpacity = source.Opacity;
            var before = profileList ? vm.Profiles.Select(row => row.Id).ToArray() : vm.ProfileMods.Select(row => row.ModId).ToArray();
            StartDrag();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(center, MouseButton.Left);
            Layout();
            Assert.False(marker.IsVisible);
            Assert.Equal(originalOpacity, source.Opacity);
            Assert.Equal(before, profileList ? vm.Profiles.Select(row => row.Id) : vm.ProfileMods.Select(row => row.ModId));
            window.MouseWheel(center, new Vector(0, -3));
            Layout();
            Assert.True(scroll.Offset.Y > 0);
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
