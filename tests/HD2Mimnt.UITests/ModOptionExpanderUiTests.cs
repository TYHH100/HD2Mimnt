using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModOptionHeader_ShouldExpandVariantsAndKeepCheckboxAndPreviewIndependent()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "本体" },
                new()
                {
                    Name = "材质包：用于检查窄窗口下很长的选项名称仍然能够展开并且勾选框保持可见",
                    SubOptions = new List<ModSubOption>
                    {
                        new() { Name = "材质 A" }, new() { Name = "材质 B" }
                    }
                }
            }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var card = vm.ModOptionsDialog!.OptionCards[1];
        using var image = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96));
        card.Image = image;
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        try
        {
            window.Show();
            main.NotificationPanelViewModel.DismissAllToasts();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            Layout();
            var surface = window.GetVisualDescendants().OfType<ProfileView>().Single()
                .FindControl<Border>("ModOptionsDialogSurface")!;
            var expander = surface.GetVisualDescendants().OfType<Expander>()
                .Single(item => item.IsEffectivelyVisible && ReferenceEquals(item.DataContext, card));
            var title = expander.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == card.Name);
            var checkbox = expander.GetVisualDescendants().OfType<CheckBox>().Single();
            var plainCheckbox = surface.GetVisualDescendants().OfType<CheckBox>()
                .Single(item => item.IsEffectivelyVisible && ReferenceEquals(item.DataContext, vm.ModOptionsDialog.OptionCards[0]));
            var chevron = expander.GetVisualDescendants().OfType<Border>()
                .Single(item => item.Name == "ExpandCollapseChevronBorder");
            var preview = expander.GetVisualDescendants().OfType<Image>().Single(item => ReferenceEquals(item.Source, image))
                .GetVisualAncestors().OfType<Border>().First();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 1000, 960 })
            {
                app.RequestedThemeVariant = theme;
                window.Width = width;
                Layout();
                Assert.False(expander.IsExpanded);
                Assert.DoesNotContain(surface.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "子选项");
                Assert.True(title.Bounds.Width > 0 && checkbox.IsEffectivelyVisible);
                var checkboxRight = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width, 0), surface)!.Value.X;
                var plainCheckboxRight = plainCheckbox.TranslatePoint(new Point(plainCheckbox.Bounds.Width, 0), surface)!.Value.X;
                Assert.InRange(Math.Abs(checkboxRight - plainCheckboxRight), 0, 1);
                Assert.True(chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), surface)!.Value.X
                    <= checkbox.TranslatePoint(default, surface)!.Value.X);
                Click(title);
                Assert.True(expander.IsExpanded);
                Assert.True(card.IsEnabled);
                Assert.True(card.SubOptions[0].IsSelected);
                await Task.Delay(200);
                Layout();
                Capture(window, theme.ToString(), $"mod-option-expanded-{width}");
                Click(checkbox);
                Assert.False(card.IsEnabled);
                Assert.True(expander.IsExpanded);
                Click(checkbox);
                Assert.True(card.IsEnabled);
                Click(preview);
                Assert.True(main.ImagePreview.IsOpen);
                Assert.True(expander.IsExpanded);
                main.ImagePreview.Close();
                Click(title);
                Assert.False(expander.IsExpanded);
                await Task.Delay(200);
                Layout();
                Capture(window, theme.ToString(), $"mod-option-collapsed-{width}");
            }
            Click(chevron);
            var choice = expander.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => ReferenceEquals(item.DataContext, card.SubOptions[1]));
            Click(choice);
            Assert.True(card.SubOptions[1].IsSelected);
            Assert.False(card.SubOptions[0].IsSelected);
            Assert.True(expander.IsExpanded);
            await vm.PersistModOptionsAsync(vm.ModOptionsDialog.TargetMod);
            var profile = await scope.Services.GetRequiredService<ProfileService>().GetProfileByIdAsync(vm.SelectedProfile!.Id);
            var saved = ProfileModOptionsState.FromJson(profile!.Mods.Single(item => item.ModId == mod.Id).OptionsJson);
            Assert.Equal(1, saved.OptionSelections![1]);
        }
        finally
        {
            main.ImagePreview.Close();
            vm.CloseModOptions();
            window.DataContext = null;
            window.Close();
            app.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public async Task ModOptionsDialog_CloseReleasesDecodedImages()
    {
        // 反复打开选项弹窗不能累积原生位图内存：关闭时取消未完成解码并释放已加载图片。
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        Directory.CreateDirectory(Path.Combine(mod.Path, "images"));
        // 用 1024×1024 原图模拟 HD2IAS 的 4K 预览图：卡片只显示 40～64 像素，不能按原图尺寸解码。
        var imagePath = Path.Combine(mod.Path, "images", "option.png");
        using (var image = new System.Drawing.Bitmap(1024, 1024))
        {
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.Clear(System.Drawing.Color.SteelBlue);
            image.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new()
                {
                    Name = "材质", GroupId = "g0", Image = "images/option.png",
                    SubOptions = new List<ModSubOption> { new() { Name = "A", Image = "images/option.png" } }
                },
                new() { Name = "模型", GroupId = "g0", Image = "images/option.png" }
            },
            OptionGroups = new List<ModOptionGroup>
            {
                new() { Id = "g0", Name = "组件", Image = "images/option.png" }
            }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var dialog = vm.ModOptionsDialog!;
        await dialog.LoadImagesAsync();
        // 卡片图片按 128 像素宽解码，1024 原图不能整张进内存。
        Assert.All(dialog.OptionCards, card =>
        {
            Assert.NotNull(card.Image);
            Assert.True(card.Image!.PixelSize.Width <= 128, card.Image.PixelSize.ToString());
        });
        Assert.All(dialog.OptionCards[0].SubOptions, sub =>
        {
            Assert.NotNull(sub.Image);
            Assert.True(sub.Image!.PixelSize.Width <= 128, sub.Image.PixelSize.ToString());
        });
        var groupImage = Assert.Single(dialog.GroupCards).Image;
        Assert.NotNull(groupImage);
        Assert.True(groupImage!.PixelSize.Width <= 128, groupImage.PixelSize.ToString());

        vm.CloseModOptionsCommand.Execute(null);

        Assert.All(dialog.OptionCards, card => Assert.True(card.IsDisposed));
        Assert.All(dialog.OptionCards, card => Assert.Null(card.Image));
        Assert.All(dialog.OptionCards[0].SubOptions, sub => Assert.Null(sub.Image));
        Assert.Null(Assert.Single(dialog.GroupCards).Image);

        // 再次打开/关闭仍可正常解码并释放，不残留上一轮位图。
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var reopened = vm.ModOptionsDialog!;
        await reopened.LoadImagesAsync();
        Assert.All(reopened.OptionCards, card => Assert.NotNull(card.Image));
        vm.CloseModOptionsCommand.Execute(null);
        Assert.All(reopened.OptionCards, card => Assert.Null(card.Image));
    }

    [AvaloniaFact]
    public async Task ModOptionsGroups_FirstLevelOpensSecondLevelAndSavesBackToFirstLevel()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "系统", GroupId = "g0", Include = new List<string> { "rig" } },
                new()
                {
                    Name = "外观", GroupId = "g1", Include = new List<string>(),
                    SubOptions = new List<ModSubOption> { new() { Name = "A" }, new() { Name = "B" } }
                }
            },
            OptionGroups = new List<ModOptionGroup>
            {
                new() { Id = "g0", Name = "系统组件" },
                new() { Id = "g1", Name = "外观组件", Description = "材质与模型" }
            }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var dialog = vm.ModOptionsDialog!;
        Assert.True(dialog.HasGroups);
        Assert.True(dialog.ShowGroupList);
        Assert.False(dialog.IsGroupPage);
        Assert.Equal(new[] { "系统组件", "外观组件" }, dialog.GroupCards.Select(group => group.Name));
        Assert.False(dialog.GroupCards[0].CanOpen);
        Assert.True(dialog.GroupCards[1].CanOpen);

        var selector = new ModOptionsSelector { DataContext = dialog };
        var window = new Window { Content = selector, Width = 720, Height = 640 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            Layout();
            // 一级：两个组件卡片各带一个开关。
            Assert.Equal(2, selector.GetVisualDescendants().OfType<CheckBox>()
                .Count(control => control.IsEffectivelyVisible));

            dialog.OpenGroupCommand.Execute(dialog.GroupCards[1]);
            Layout();
            Assert.True(dialog.IsGroupPage);
            Assert.Single(dialog.VisibleOptionCards);
            Assert.Equal("外观", dialog.VisibleOptionCards[0].Name);
            Assert.Equal(1, selector.GetVisualDescendants().OfType<CheckBox>()
                .Count(control => control.IsEffectivelyVisible));

            dialog.SelectSubOption(dialog.VisibleOptionCards[0].SubOptions[1]);
            dialog.ReturnToGroupsCommand.Execute(null);
            Layout();
            Assert.False(dialog.IsGroupPage);
            Assert.True(dialog.ShowGroupList);

            // 保存后回到一级且选择已持久化到当前配置。
            bool saved = false;
            for (var i = 0; i < 100 && !saved; i++)
            {
                var profile = await scope.Services.GetRequiredService<ProfileService>()
                    .GetProfileByIdAsync(vm.SelectedProfile!.Id);
                var state = ProfileModOptionsState.FromJson(
                    profile!.Mods.Single(item => item.ModId == mod.Id).OptionsJson);
                saved = state.OptionSelections is { Count: > 1 } && state.OptionSelections[1] == 1;
                if (!saved) await Task.Delay(10);
            }
            Assert.True(saved);
        }
        finally
        {
            vm.CloseModOptions();
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ModOptionsDialog_PreviewOriginalIsDecodedOnDemandAndReleased()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        Directory.CreateDirectory(Path.Combine(mod.Path, "images"));
        var imagePath = Path.Combine(mod.Path, "images", "option.png");
        using (var image = new System.Drawing.Bitmap(1024, 1024))
        {
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.Clear(System.Drawing.Color.SteelBlue);
            image.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption> { new() { Name = "材质", Image = "images/option.png" } }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var dialog = vm.ModOptionsDialog!;
        await dialog.LoadImagesAsync();
        var card = dialog.OptionCards[0];
        Assert.NotNull(card.Image);
        Assert.True(card.Image!.PixelSize.Width <= 128, card.Image.PixelSize.ToString());
        try
        {
            // 点击卡片图片才按原图尺寸解码；关闭弹窗后覆盖层随之关闭并释放。
            dialog.ShowImagePreview(card.Image, card.Name);
            for (var i = 0; i < 400 && !main.ImagePreview.IsOpen; i++) await Task.Delay(10);
            Assert.True(main.ImagePreview.IsOpen);
            var preview = main.ImagePreview.Image;
            Assert.NotNull(preview);
            Assert.Equal(1024, preview!.PixelSize.Width);
            vm.CloseModOptionsCommand.Execute(null);
            Assert.False(main.ImagePreview.IsOpen);
        }
        finally
        {
            main.ImagePreview.Close();
            vm.CloseModOptions();
        }
    }
}
