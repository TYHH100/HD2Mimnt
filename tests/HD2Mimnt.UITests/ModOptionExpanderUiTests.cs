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
}
