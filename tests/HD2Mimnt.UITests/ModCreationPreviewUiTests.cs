using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCreationImages_ShouldOpenSharedPreviewForCoverOptionAndSubOption()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        var source = Path.Combine(scope.Root, "editor-images"); Directory.CreateDirectory(source);
        foreach (var (file, width, height) in new[] { ("cover.png", 16, 16), ("option.png", 24, 12), ("child.png", 12, 24) })
        {
            using var image = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.Clear(System.Drawing.Color.Red);
            image.Save(Path.Combine(source, file), System.Drawing.Imaging.ImageFormat.Png);
        }
        vm.SourceDirectory = source; await vm.InitializeSourceAsync();
        vm.ModName = "图片确认"; vm.IconPath = "cover.png";
        vm.AddOptionCommand.Execute(null);
        var option = vm.SelectedOption!; option.Name = "选项图"; option.ImagePath = "option.png";
        option.AddSubOptionCommand.Execute(null);
        var child = option.SubOptions.Single(); child.Name = "子选项图"; child.ImagePath = "child.png";
        vm.IsDirty = false; var originalJson = vm.JsonPreview;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, (window.InputHitTest(point) as Visual)!.GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            async Task WaitForImage()
            {
                for (var i = 0; i < 200 && vm.IsBusy; i++) { await Task.Delay(20); Layout(); }
                Assert.False(vm.IsBusy, vm.StatusMessage); Layout();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var tabs = view.FindControl<TabControl>("CreationTabs")!;
            foreach (var (buttonName, tab, size, title) in new[]
            {
                ("CreationCoverPreviewButton", 0, new PixelSize(16, 16), vm.ModName),
                ("CreationOptionPreviewButton", 1, new PixelSize(24, 12), option.Name),
                ("CreationSubOptionPreviewButton", 1, new PixelSize(12, 24), child.Name)
            })
            {
                tabs.SelectedIndex = tab; Layout();
                var button = view.GetVisualDescendants().OfType<Button>().Single(item => item.Name == buttonName);
                Click(button); await WaitForImage();
                Assert.True(main.ImagePreview.IsOpen);
                Assert.Equal(title, main.ImagePreview.Title);
                Assert.Equal(size, main.ImagePreview.Image!.PixelSize);
                var overlay = window.GetVisualDescendants().OfType<ImagePreviewOverlay>().Single();
                Assert.Same(main.ImagePreview, overlay.DataContext);
                Assert.True(overlay.FindControl<Border>("Root")!.IsEffectivelyVisible);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
                Assert.False(main.ImagePreview.IsOpen); Assert.Null(main.ImagePreview.Image);
            }
            Assert.False(vm.IsDirty); Assert.Equal(originalJson, vm.JsonPreview);
            child.ImagePath = "missing.png";
            Click(view.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "CreationSubOptionPreviewButton"));
            await WaitForImage(); Assert.False(main.ImagePreview.IsOpen);
            Assert.Contains(UiLocalization.Current["Creator.ImageUnavailable"], vm.StatusMessage);
            await vm.PreviewImageAsync("cover.png", vm.ModName);
            Assert.True(main.ImagePreview.IsOpen);
            await vm.StopAsync(); Assert.False(main.ImagePreview.IsOpen);
        }
        finally { await vm.StopAsync(); main.ImagePreview.Close(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ModCreationPreview_ShouldSelectWithoutEditingAndExportAllChildren()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        vm.ModName = "预览模组"; vm.Description = "完整选项预览：勾选独立选项，点击标题展开子项。";
        Assert.Equal("预览模组.7z", vm.ArchiveFileName);
        vm.SelectedArchiveFormatIndex = 1; Assert.Equal("预览模组.zip", vm.ArchiveFileName);
        vm.ModName = "路径/名称:*?"; Assert.Equal("路径_名称___", vm.ExportBaseName);
        vm.ModName = "预览模组";
        var source = Path.Combine(scope.Root, "preview"); Directory.CreateDirectory(source);
        var imagePath = Path.Combine(source, "cover.png");
        using (var image = new System.Drawing.Bitmap(16, 16))
        {
            using var graphics = System.Drawing.Graphics.FromImage(image); graphics.Clear(System.Drawing.Color.Red);
            image.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        vm.SourceDirectory = source; await vm.InitializeSourceAsync();
        vm.IconPath = "cover.png";
        for (var i = 0; i < 3; i++)
        {
            vm.AddOptionCommand.Execute(null); var option = vm.SelectedOption!;
            option.Name = "选项 " + i; option.ImagePath = "cover.png";
            option.DefaultEnabled = i != 1;
            for (var j = 0; j < 7; j++)
            {
                option.AddSubOptionCommand.Execute(null); var sub = option.SubOptions.Last();
                sub.Name = $"子项 {i}-{j}"; sub.Description = "子项完整描述"; sub.ImagePath = "cover.png";
            }
        }
        vm.IsDirty = false; var originalJson = vm.JsonPreview;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        var app = Avalonia.Application.Current!; var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            view.FindControl<TabControl>("CreationTabs")!.SelectedIndex = 3;
            for (var i = 0; i < 200 && (vm.IsBusy || vm.ManagerPreview == null); i++) { await Task.Delay(20); Layout(); }
            Assert.False(vm.IsBusy, vm.StatusMessage);
            var preview = vm.ManagerPreview!; Assert.NotNull(preview); Assert.NotNull(preview.Cover);
            Assert.All(preview.OptionCards, card => Assert.NotNull(card.Image));
            Assert.False(preview.OptionCards[1].IsEnabled);
            var content = view.FindControl<ModCreationPreview>("CreationManagerPreview")!;
            var expanders = content.GetVisualDescendants().OfType<Expander>().Where(item => item.IsVisible).ToList();
            Assert.Equal(3, expanders.Count); Assert.All(expanders, item => Assert.False(item.IsExpanded));
            var box = content.GetVisualDescendants().OfType<CheckBox>().First(item => item.IsEffectivelyVisible);
            Click(box); Assert.False(preview.OptionCards[0].IsEnabled);
            expanders[0].IsExpanded = true; Layout();
            var choice = content.GetVisualDescendants().OfType<ListBoxItem>().Single(item => ReferenceEquals(item.DataContext, preview.OptionCards[0].SubOptions[1]));
            Click(choice); Assert.True(preview.OptionCards[0].SubOptions[1].IsSelected);
            Assert.False(preview.OptionCards[0].SubOptions[0].IsSelected);
            expanders[0].IsExpanded = false; Layout();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme; Layout();
                var path = Path.Combine(scope.Root, theme + ".png");
                await vm.ExportScreenshotAsync(path, view.RenderScreenshotAsync);
                Assert.True(File.Exists(path), vm.StatusMessage);
                using var bitmap = new Bitmap(path);
                Assert.Equal(760, bitmap.PixelSize.Width); Assert.True(bitmap.PixelSize.Height > 1600, bitmap.PixelSize.ToString());
                using var check = new System.Drawing.Bitmap(path);
                Assert.Equal(255, check.GetPixel(0, check.Height - 1).A);
                // 最后一个子项图像必须是纯红色，不能截到展开动画的渐显中间帧。
                var redPixels = 0;
                for (var y = check.Height - 100; y < check.Height - 24; y++)
                    if (check.GetPixel(60, y).R == 255 && check.GetPixel(60, y).G == 0) redPixels++;
                Assert.True(redPixels >= 35, $"末项完整图片高度只有 {redPixels}");
                var output = Environment.GetEnvironmentVariable("HD2MIMNT_UI_PREVIEW_PATH");
                if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.Copy(path, Path.Combine(output, "creation-long-" + theme + ".png"), true); }
            }
            Assert.All(expanders, item => Assert.False(item.IsExpanded));
            Assert.Same(preview, vm.ManagerPreview); Assert.False(preview.OptionCards[0].IsEnabled);
            Assert.False(vm.IsDirty); Assert.Equal(originalJson, vm.JsonPreview);
            Assert.Empty(view.FindControl<Canvas>("CreationScreenshotHost")!.Children);
            vm.IsV1Manifest = false; vm.LegacyOptions.Clear(); vm.LegacyOptions.Add("红色"); vm.LegacyOptions.Add("蓝色");
            vm.IsDirty = false; await vm.RefreshManagerPreviewAsync(); Layout();
            Assert.True(vm.ManagerPreview!.IsLegacyMode);
            var legacyRow = content.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                ReferenceEquals(item.DataContext, vm.ManagerPreview.LegacyChoices[1]));
            Click(legacyRow); Assert.True(vm.ManagerPreview.LegacyChoices[1].IsSelected);
            Assert.False(vm.ManagerPreview.LegacyChoices[0].IsSelected);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language); Layout();
                Assert.Equal(UiLocalization.Current["Creator.ExportScreenshot"], view.FindControl<Button>("ExportCreationScreenshotButton")!.Content);
            }
            var legacyPath = Path.Combine(scope.Root, "legacy.png");
            await vm.ExportScreenshotAsync(legacyPath, view.RenderScreenshotAsync);
            Assert.True(File.Exists(legacyPath), vm.StatusMessage);
            Assert.False(vm.IsDirty);
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }
}
