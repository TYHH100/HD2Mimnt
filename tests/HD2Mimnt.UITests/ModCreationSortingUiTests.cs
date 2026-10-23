using System.IO.Compression;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCreationSorting_ShouldDragBothLevelsScrollCancelAndPreserveExportedMetadata()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        var source = Path.Combine(scope.Root, "sorting-source"); Directory.CreateDirectory(source);
        var path = Path.Combine(source, "manifest.json");
        var original = new JsonObject
        {
            ["Version"] = 1, ["Name"] = "排序测试", ["Guid"] = Guid.NewGuid().ToString(),
            ["Options"] = new JsonArray(Enumerable.Range(0, 24).Select(index => (JsonNode)new JsonObject
            {
                ["Name"] = $"选项 {index}", ["OptionMarker"] = index, ["Include"] = new JsonArray(),
                ["SubOptions"] = index == 0 ? new JsonArray(Enumerable.Range(0, 3).Select(child => (JsonNode)new JsonObject
                { ["Name"] = $"子选项 {child}", ["SubMarker"] = child, ["Include"] = new JsonArray() }).ToArray()) : null
            }).ToArray())
        };
        await File.WriteAllTextAsync(path, original.ToJsonString()); await vm.OpenAsync(path);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 900 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var tabs = view.FindControl<TabControl>("CreationTabs")!;
            tabs.SelectedIndex = 1; Layout();
            var list = view.FindControl<ListBox>("CreationOptionList")!;
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var marker = view.FindControl<Border>("CreationSortMarker")!;
            IPointer? activePointer = null;
            view.AddHandler(InputElement.PointerMovedEvent, (_, args) => activePointer = args.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            var parent = vm.Options[0];
            Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Border Handle(object item) => view.GetVisualDescendants().OfType<Border>().Single(control =>
                control.Classes.Contains("creation-sort-handle") && ReferenceEquals(control.DataContext, item));
            void Begin(Control control)
            {
                control.BringIntoView(); Layout();
                var point = Center(control);
                Assert.Contains(control, (window.InputHitTest(point) as Visual)!.GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(point + new Vector(0, 8), RawInputModifiers.LeftMouseButton); Layout();
                Assert.True(marker.IsVisible);
            }
            void Escape()
            {
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
                Assert.False(marker.IsVisible);
            }
            Begin(Handle(parent)); Assert.False(vm.IsDirty);
            Assert.Same(parent, vm.Options[0]);
            var center = Center(scroll);
            window.MouseMove(center, RawInputModifiers.LeftMouseButton);
            for (var step = 0; step < 8; step++) { window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.LeftMouseButton); Layout(); }
            var down = scroll.Offset.Y; Assert.True(down > 0);
            window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.LeftMouseButton); Layout(); Assert.True(scroll.Offset.Y < down);
            window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.LeftMouseButton); Layout();
            foreach (var container in list.GetRealizedContainers())
                Assert.Equal(ReferenceEquals(container.DataContext, parent) ? 0.35 : 1, container.Opacity);
            var last = (Control)list.ContainerFromIndex(list.ItemCount - 1)!;
            var end = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height - 2), window)!.Value;
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); Layout(); Assert.True(marker.IsVisible);
            Assert.Same(parent, vm.Options[0]);
            window.MouseUp(end, MouseButton.Left); Layout();
            Assert.Same(parent, vm.Options[^1]); Assert.Same(parent, vm.SelectedOption);
            Assert.True(vm.IsDirty); Assert.False(marker.IsVisible);
            await vm.SaveAsync(path); Assert.False(vm.IsDirty);

            // Escape 走窗口的实际快捷键入口，不能在松开后提交被取消的排序。
            var before = vm.Options.ToArray();
            Begin(Handle(parent));
            window.MouseMove(Center(scroll), RawInputModifiers.LeftMouseButton); Layout(); Escape();
            window.MouseUp(Center(scroll), MouseButton.Left); Layout();
            Assert.Equal(before, vm.Options); Assert.False(vm.IsDirty);
            Assert.All(list.GetRealizedContainers(), container => Assert.Equal(1, container.Opacity));

            var children = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "CreationSubOptionList");
            var editorScroll = view.FindControl<ScrollViewer>("CreationOptionsScroll")!;
            var child = parent.SubOptions[0];
            Begin(Handle(child));
            center = Center(editorScroll); window.MouseMove(center, RawInputModifiers.LeftMouseButton); Layout();
            for (var step = 0; step < 8; step++) { window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.LeftMouseButton); Layout(); }
            down = editorScroll.Offset.Y;
            window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.LeftMouseButton); Layout(); Assert.True(editorScroll.Offset.Y < down);
            window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.LeftMouseButton); Layout();
            last = (Control)children.ContainerFromIndex(children.ItemCount - 1)!;
            end = last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height - 2), window)!.Value;
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); Layout(); Assert.True(marker.IsVisible);
            Assert.Same(child, parent.SubOptions[0]);
            window.MouseUp(end, MouseButton.Left); Layout();
            Assert.Same(child, parent.SubOptions[^1]); Assert.True(vm.IsDirty);
            Assert.All(children.GetRealizedContainers(), container => Assert.Equal(1, container.Opacity));
            await vm.SaveAsync(path); Assert.False(vm.IsDirty);

            var childOrder = parent.SubOptions.ToArray();
            Begin(Handle(parent.SubOptions[0])); Escape(); window.MouseUp(center, MouseButton.Left); Layout();
            Assert.Equal(childOrder, parent.SubOptions); Assert.False(vm.IsDirty);
            // 越过左侧选项列表不会把子选项迁到另一组选项。
            Begin(Handle(parent.SubOptions[0]));
            window.MouseMove(Center(list), RawInputModifiers.LeftMouseButton); Layout(); Assert.False(marker.IsVisible);
            window.MouseUp(Center(list), MouseButton.Left); Layout();
            Assert.Equal(childOrder, parent.SubOptions); Assert.Same(parent, vm.SelectedOption);

            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, ManifestPackage.MetadataFileName)))!;
            Assert.All(saved["Options"]!.AsArray(), node => Assert.Null(node!["OptionMarker"]));
            Assert.Equal(Enumerable.Range(1, 23).Append(0), metadata["Extensions"]!["Options"]!.AsArray().Select(node => node!["OptionMarker"]!.GetValue<int>()));
            Assert.Equal(new[] { 1, 2, 0 }, metadata["Extensions"]!["Options"]![23]!["SubOptions"]!.AsArray().Select(node => node!["SubMarker"]!.GetValue<int>()));
            Assert.Equal(metadata.ToJsonString(), JsonNode.Parse(vm.MetadataPreview)!.ToJsonString());
            Assert.Equal(saved["Options"]!.ToJsonString(), JsonNode.Parse(vm.JsonPreview)!["Options"]!.ToJsonString());
            var zip = Path.Combine(scope.Root, "sorted.zip"); await vm.ExportAsync(zip); Assert.True(File.Exists(zip), vm.StatusMessage);
            using (var archive = ZipFile.OpenRead(zip))
            {
                using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
                    Assert.Equal(saved["Options"]!.ToJsonString(), JsonNode.Parse(await reader.ReadToEndAsync())!["Options"]!.ToJsonString());
                using (var reader = new StreamReader(archive.GetEntry(ManifestPackage.MetadataFileName)!.Open()))
                    Assert.Equal(metadata.ToJsonString(), JsonNode.Parse(await reader.ReadToEndAsync())!.ToJsonString());
            }

            // 向上拖回首位，覆盖移除源项前后的槽位换算。
            Begin(Handle(parent)); center = Center(scroll);
            window.MouseMove(center, RawInputModifiers.LeftMouseButton);
            for (var step = 0; step < 10; step++) { window.MouseWheel(center, new Vector(0, 3), RawInputModifiers.LeftMouseButton); Layout(); }
            var first = (Control)list.ContainerFromIndex(0)!;
            end = first.TranslatePoint(new Point(first.Bounds.Width / 2, 2), window)!.Value;
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); Layout();
            Assert.Same(parent, vm.Options[0]); Assert.Same(parent, vm.SelectedOption);
            children = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "CreationSubOptionList");
            Begin(Handle(child)); center = Center(editorScroll);
            window.MouseMove(center, RawInputModifiers.LeftMouseButton);
            for (var step = 0; step < 10; step++) { window.MouseWheel(center, new Vector(0, 3), RawInputModifiers.LeftMouseButton); Layout(); }
            // 首个子选项可位于编辑器下半部，滚动至其标题后拖到卡片顶部。
            first = (Control)children.ContainerFromIndex(0)!;
            first.BringIntoView(); Layout();
            end = first.TranslatePoint(new Point(first.Bounds.Width / 2, 2), window)!.Value;
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); Layout();
            Assert.Same(child, parent.SubOptions[0]);
            await vm.SaveAsync(path); Assert.False(vm.IsDirty);

            // 表单输入不启动拖动；同一位置点击也不把草稿标为已修改。
            var editor = view.GetVisualDescendants().OfType<TextBox>().Single(control =>
                ReferenceEquals(control.DataContext, parent.SubOptions[0]) && control.Text == parent.SubOptions[0].Name);
            editor.BringIntoView(); Layout();
            end = Center(editor); window.MouseDown(end, MouseButton.Left);
            window.MouseMove(end + new Vector(8, 0), RawInputModifiers.LeftMouseButton); Layout(); Assert.False(marker.IsVisible);
            window.MouseUp(end + new Vector(8, 0), MouseButton.Left); Layout(); Assert.False(vm.IsDirty);
            var singleClick = Center(Handle(parent));
            window.MouseDown(singleClick, MouseButton.Left); window.MouseUp(singleClick, MouseButton.Left); Layout(); Assert.False(vm.IsDirty);

            // 捕获丢失与内部标签切换都必须取消，并允许后续继续拖动。
            Begin(Handle(parent));
            Assert.NotNull(activePointer);
            activePointer.Capture(null); Layout(); Assert.False(marker.IsVisible);
            window.MouseUp(Center(list), MouseButton.Left); Assert.False(vm.IsDirty);
            Begin(Handle(parent)); tabs.SelectedIndex = 0; Layout(); Assert.False(marker.IsVisible);
            window.MouseUp(new Point(50, 200), MouseButton.Left);
            tabs.SelectedIndex = 1; Layout();

            // 页面卸载也必须清理插入线、源透明度和捕获。
            Begin(Handle(parent)); end = Center(list);
            main.NavigateToLibraryCommand.Execute(null); Layout(); Assert.False(marker.IsVisible);
            window.MouseUp(end, MouseButton.Left); Assert.False(vm.IsDirty);
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); }
    }
}
