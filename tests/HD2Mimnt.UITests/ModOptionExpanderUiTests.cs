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
    public async Task ModOptionsGroup_InnerOptionToggle_KeepsComponentCheckedWhileAnyOptionEnabled()
    {
        // 回归：二级页关闭任意一个选项后，一级组件不应被误显示为取消勾选。
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new() { Name = "外观 A", GroupId = "g0", Include = new List<string>() },
                new() { Name = "外观 B", GroupId = "g0", Include = new List<string>() }
            },
            OptionGroups = new List<ModOptionGroup> { new() { Id = "g0", Name = "模型组件" } }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var dialog = vm.ModOptionsDialog!;
        var group = Assert.Single(dialog.GroupCards);
        Assert.True(group.IsEnabled);

        dialog.OpenGroupCommand.Execute(group);
        Assert.Equal(2, dialog.VisibleOptionCards.Count);
        dialog.ToggleOptionCommand.Execute(dialog.VisibleOptionCards[0]);
        Assert.False(dialog.VisibleOptionCards[0].IsEnabled);
        // 关键断言：只关闭部分选项时组件仍参与部署，外层保持勾选。
        Assert.True(group.IsEnabled);
        dialog.ReturnToGroupsCommand.Execute(null);
        Assert.False(dialog.IsGroupPage);
        Assert.True(group.IsEnabled);

        // 组件内所有选项都关闭时，组件确实不参与部署，才显示未勾选。
        dialog.OpenGroupCommand.Execute(group);
        dialog.ToggleOptionCommand.Execute(dialog.VisibleOptionCards[1]);
        Assert.False(group.IsEnabled);
        dialog.ReturnToGroupsCommand.Execute(null);
        Assert.False(group.IsEnabled);

        // 一级重新勾选组件应恢复组内全部选项。
        dialog.ToggleGroupCommand.Execute(group);
        Assert.True(group.IsEnabled);
        Assert.All(group.Options, option => Assert.True(option.IsEnabled));

        // 保存并返回后再次打开，一级勾选状态仍与内部选项一致。
        dialog.ReturnToGroupsCommand.Execute(null);
        vm.CloseModOptionsCommand.Execute(null);
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var reopened = vm.ModOptionsDialog!;
        Assert.True(Assert.Single(reopened.GroupCards).IsEnabled);
        bool saved = false;
        for (var i = 0; i < 100 && !saved; i++)
        {
            var profile = await scope.Services.GetRequiredService<ProfileService>()
                .GetProfileByIdAsync(vm.SelectedProfile!.Id);
            var state = ProfileModOptionsState.FromJson(
                profile!.Mods.Single(item => item.ModId == mod.Id).OptionsJson);
            saved = state.OptionToggles is { Count: 2 } toggles && toggles.All(value => value);
            if (!saved) await Task.Delay(10);
        }
        Assert.True(saved);
        vm.CloseModOptionsCommand.Execute(null);
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

    [AvaloniaFact]
    public async Task LooseFolderCollection_ShowsComponentListAndSecondLevelOptions()
    {
        // 用户反馈：散装文件夹（外层无清单、子目录各自是模组）添加后要在配置页显示两级，
        // 一级是子模组组件，点进去二级配置它自己的选项。
        await using var scope = await UiScope.CreateAsync();
        var source = Path.Combine(scope.Root, "Shinra战斗女仆替换RS系列");
        CreateCollectionChild(source, "RS-100“制裁者”潜行轻甲", "Shinra战斗女仆替换RS-67“空密码”潜行中甲");
        CreateCollectionChild(source, "RS-67“空密码”潜行中甲", "Shinra战斗女仆替换RS-67“空密码”潜行中甲");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var id = await scope.Services.GetRequiredService<ModService>().ImportModAsync(source);
        var profile = await profiles.GetActiveProfileAsync();
        if (profile == null)
        {
            var profileId = await profiles.CreateProfileAsync("合集测试", "散装文件夹合集");
            await profiles.SetActiveProfileAsync(profileId);
            profile = (await profiles.GetActiveProfileAsync())!;
        }
        await profiles.AddModToProfileAsync(profile.Id, id);
        var vm = main.ProfileViewModel;
        await vm.RefreshAfterProfilesChangedAsync();
        var row = vm.ProfileMods.Single(item => item.ModId == id);
        Assert.True(row.HasOptions);

        vm.OpenModOptionsCommand.Execute(row);
        var dialog = vm.ModOptionsDialog!;
        try
        {
            Assert.True(dialog.HasGroups);
            Assert.True(dialog.ShowGroupList);
            Assert.False(dialog.IsGroupPage);
            Assert.Equal(new[] { "RS-100“制裁者”潜行轻甲", "RS-67“空密码”潜行中甲" },
                dialog.GroupCards.Select(group => group.Name));
            Assert.True(dialog.GroupCards[0].CanOpen);

            dialog.OpenGroupCommand.Execute(dialog.GroupCards[0]);
            Assert.True(dialog.IsGroupPage);
            Assert.Equal(2, dialog.VisibleOptionCards.Count);
            Assert.Equal("本体", dialog.VisibleOptionCards[0].Name);

            // 二级关闭一个选项：只排除该选项文件，一级组件仍保持勾选，并保存到当前配置。
            dialog.ToggleOptionCommand.Execute(dialog.VisibleOptionCards[1]);
            dialog.ReturnToGroupsCommand.Execute(null);
            Assert.False(dialog.IsGroupPage);
            Assert.True(dialog.GroupCards[0].IsEnabled);
            bool saved = false;
            for (var i = 0; i < 100 && !saved; i++)
            {
                var snapshot = await profiles.GetProfileByIdAsync(profile.Id);
                var state = ProfileModOptionsState.FromJson(
                    snapshot!.Mods.Single(item => item.ModId == id).OptionsJson);
                saved = state.OptionToggles is { Count: 4 } toggles && !toggles[1] && toggles[0];
                if (!saved) await Task.Delay(10);
            }
            Assert.True(saved);
        }
        finally
        {
            vm.CloseModOptionsCommand.Execute(null);
        }
    }

    private static void CreateCollectionChild(string parent, string directoryName, string manifestName)
    {
        var child = Path.Combine(parent, directoryName);
        foreach (var optionDirectory in new[] { "本体", "材质包" })
        {
            Directory.CreateDirectory(Path.Combine(child, optionDirectory));
            File.WriteAllText(Path.Combine(child, optionDirectory, "9ba626afa44a3aa3.patch_0"),
                $"{directoryName}-{optionDirectory}");
            using (var image = new System.Drawing.Bitmap(64, 64))
            {
                using var graphics = System.Drawing.Graphics.FromImage(image);
                graphics.Clear(System.Drawing.Color.SteelBlue);
                image.Save(Path.Combine(child, optionDirectory, "1.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        using (var icon = new System.Drawing.Bitmap(64, 64))
        {
            using var graphics = System.Drawing.Graphics.FromImage(icon);
            graphics.Clear(System.Drawing.Color.DarkOrange);
            icon.Save(Path.Combine(child, "1.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(child, "manifest.json"), $$"""
            {
              "Version": 1,
              "Guid": "{{Guid.NewGuid()}}",
              "Name": "{{manifestName}}",
              "Description": "合集组件",
              "IconPath": "1.png",
              "Options": [
                { "Name": "本体", "Description": "必选", "Image": "本体/1.png", "Include": ["本体"] },
                { "Name": "材质包", "Description": "可选", "Image": "材质包/1.png", "Include": ["材质包"] }
              ]
            }
            """);
    }

    [AvaloniaFact]
    public async Task ModOptionsDialog_IdenticalImageContent_IsDecodedOnceAndReused()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var mod = (await repository.GetAllAsync()).First();
        var imageDir = Path.Combine(mod.Path, "images");
        Directory.CreateDirectory(imageDir);
        // 两个路径、内容完全相同的副本，模拟 HD2IAS 组件目录里复制出来的同一张预览图。
        var firstPath = Path.Combine(imageDir, "a.png");
        using (var image = new System.Drawing.Bitmap(1024, 1024))
        {
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.Clear(System.Drawing.Color.SteelBlue);
            image.Save(firstPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        var secondPath = Path.Combine(imageDir, "b.png");
        File.Copy(firstPath, secondPath);
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new()
                {
                    Name = "材质", Image = "images/a.png",
                    SubOptions = new List<ModSubOption> { new() { Name = "变体", Image = "images/b.png" } }
                },
                new() { Name = "模型", Image = "images/b.png" }
            }
        };
        await repository.UpdateAsync(mod);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        vm.OpenModOptionsCommand.Execute(vm.ProfileMods.Single(item => item.ModId == mod.Id));
        var dialog = vm.ModOptionsDialog!;
        try
        {
            await dialog.LoadImagesAsync();
            var first = dialog.OptionCards[0];
            var second = dialog.OptionCards[1];
            var sub = first.SubOptions[0];
            Assert.NotNull(first.Image);
            Assert.NotNull(second.Image);
            Assert.NotNull(sub.Image);
            // 内容哈希相同：两个路径只解码一张位图，卡片/子选项共享同一实例。
            Assert.Same(first.Image, second.Image);
            Assert.Same(first.Image, sub.Image);

            // 内容相同的另一份副本打开原图预览时也应复用，不重复解码整张 4K 原图。
            dialog.ShowImagePreview(first.Image, first.Name);
            for (var i = 0; i < 400 && !main.ImagePreview.IsOpen; i++) await Task.Delay(10);
            Assert.True(main.ImagePreview.IsOpen);
            var original = main.ImagePreview.Image;
            Assert.NotNull(original);
            Assert.Equal(1024, original!.PixelSize.Width);

            dialog.ShowImagePreview(second.Image, second.Name);
            for (var i = 0; i < 400; i++)
            {
                if (ReferenceEquals(main.ImagePreview.Image, original)) break;
                await Task.Delay(10);
            }
            Assert.Same(original, main.ImagePreview.Image);

            vm.CloseModOptionsCommand.Execute(null);
            Assert.False(main.ImagePreview.IsOpen);
            Assert.All(dialog.OptionCards, card => Assert.Null(card.Image));
        }
        finally
        {
            main.ImagePreview.Close();
            vm.CloseModOptions();
        }
    }
}
