using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictNavigation_RepeatedJumpsAndReturnToTop_ShouldPreserveAllRowPositions(bool hasSeparators)
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var data = scope.Services.GetRequiredService<TestDataService>();
        await data.ImportGeneratedModsAsync(Enumerable.Range(1, 64).Select(index => new Mod
        {
            Name = $"HD2Mimnt Test Scroll {index:00} 长名称盔甲替换与冲突跳转回归", Author = "Helldivers", Version = "1.0.0",
            Path = Path.Combine(scope.Root, "scroll", index.ToString())
        }));
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profile = (await profiles.GetActiveProfileAsync())!;
        foreach (var mod in (await data.GetAllModsAsync()).Where(mod => !profile.Mods.Any(item => item.ModId == mod.Id)))
            await profiles.AddModToProfileAsync(profile.Id, mod.Id);
        if (hasSeparators)
        {
            await profiles.AddSeparatorAsync(profile.Id, "顶部盔甲分组", 0);
            await profiles.AddSeparatorAsync(profile.Id, "中间分组", 30);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var source = vm.ProfileMods[0];
        var targets = new[] { vm.ProfileMods[^1], vm.ProfileMods[35], vm.ProfileMods[15] };
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 830 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var scroll = view.FindControl<ScrollViewer>("ModScrollViewer")!;
            var items = view.FindControl<ItemsControl>("ModItemsControl")!;
            double RowTop(int index) => items.ContainerFromIndex(index)?.TranslatePoint(default, scroll)?.Y ?? double.NaN;
            var initialTop = RowTop(0);
            var initialModTop = RowTop(1);
            async Task Jump(ProfileModViewModel target, bool picker)
            {
                vm.ApplyConflictResults([new ConflictGroupViewModel(new ConflictGroup
                {
                    ResourceId = 42, TypeId = 0xcd4238c6a0c69e32UL,
                    Owners = (picker ? new[] { source, target, vm.ProfileMods[10] } : new[] { source, target })
                        .Select(mod => new ConflictFileOwner { ModId = mod.ModId, ModName = mod.ModName }).ToArray()
                })]);
                Layout();
                void Click(Button button)
                {
                    using var frame = window.CaptureRenderedFrame();
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    var hit = window.InputHitTest(point) as Visual;
                    Assert.True(hit == button || hit?.GetVisualAncestors().Contains(button) == true);
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Layout();
                }
                Click(view.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "NavigateConflictButton" && button.DataContext == source));
                await vm.OpenConflictNavigationCommand.ExecutionTask!;
                Layout();
                if (picker)
                {
                    var surface = view.FindControl<Border>("ConflictModPickerSurface")!;
                    Click(surface.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter == target));
                    await vm.LocateConflictModCommand.ExecutionTask!;
                }
                Layout();
                var container = Assert.IsAssignableFrom<Control>(items.ContainerFromIndex(vm.ProfileRows.IndexOf(target)));
                var top = container.TranslatePoint(default, scroll)!.Value.Y;
                Assert.True(top >= -1 && top + container.Bounds.Height <= scroll.Viewport.Height + 1,
                    $"目标未完全显示：top={top}, height={container.Bounds.Height}, viewport={scroll.Viewport}");
                Assert.True(target.IsConflictLocation);
                Assert.True(items.GetRealizedContainers().Count() < 30, "跳转后仍应保持虚拟化");
            }
            for (var iteration = 0; iteration < 9; iteration++)
            {
                app.RequestedThemeVariant = iteration % 2 == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
                window.Width = iteration % 2 == 0 ? 1280 : 1000;
                Layout();
                await Jump(targets[iteration % targets.Length], picker: iteration % 2 == 0);
                var wheelPoint = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
                for (var step = 0; step < 100 && scroll.Offset.Y > 0; step++)
                {
                    using var frame = window.CaptureRenderedFrame();
                    window.MouseWheel(wheelPoint, new Vector(0, 3));
                    Layout();
                }
                Assert.Equal(0, scroll.Offset.Y);
                Assert.True(Math.Abs(RowTop(0) - initialTop) <= 1,
                    $"第 {iteration + 1} 次跳转后分隔符偏移：初始={initialTop}, 当前={RowTop(0)}");
                Assert.True(Math.Abs(RowTop(1) - initialModTop) <= 1,
                    $"第 {iteration + 1} 次跳转后普通卡片偏移：初始={initialModTop}, 当前={RowTop(1)}");
                if (iteration < 2)
                    Capture(window, app.RequestedThemeVariant.ToString()!, $"conflict-navigation-return-top-{hasSeparators}");
            }
        }
        finally { app.RequestedThemeVariant = originalTheme; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryNavigation_RepeatedEndAndWheelToTop_ShouldPreserveFirstCardPosition()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        await scope.Services.GetRequiredService<TestDataService>().ImportGeneratedModsAsync(Enumerable.Range(1, 64).Select(index => new Mod
        {
            Name = $"HD2Mimnt Test Library Scroll {index:00}", Author = "Helldivers", Version = "1.0.0",
            Path = Path.Combine(scope.Root, "scroll", index.ToString())
        }));
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var items = view.FindControl<ItemsControl>("ModList")!;
            var scroll = items.GetVisualAncestors().OfType<ScrollViewer>().First();
            double FirstTop() => items.ContainerFromIndex(0)?.TranslatePoint(default, scroll)?.Y ?? double.NaN;
            var initialTop = FirstTop();
            items.Focus();
            for (var iteration = 0; iteration < 3; iteration++)
            {
                window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                window.KeyRelease(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                Layout();
                Assert.Same(main.LibraryViewModel.FilteredMods[^1], main.LibraryViewModel.FocusedMod);
                Assert.True(scroll.Offset.Y > 0);
                var point = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
                for (var step = 0; step < 100 && scroll.Offset.Y > 0; step++)
                {
                    using var frame = window.CaptureRenderedFrame();
                    window.MouseWheel(point, new Vector(0, 3));
                    Layout();
                }
                Assert.Equal(0, scroll.Offset.Y);
                Assert.True(Math.Abs(FirstTop() - initialTop) <= 1, $"模组库顶部偏移：初始={initialTop}, 当前={FirstTop()}");
                Assert.True(items.GetRealizedContainers().Count() < 30);
            }
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
