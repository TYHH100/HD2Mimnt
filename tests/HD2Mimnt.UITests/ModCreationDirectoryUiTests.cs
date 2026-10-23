using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCreationDirectories_ShouldAutofillAndChooseDirectoriesWithCheckboxes()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        var app = Avalonia.Application.Current!; var originalTheme = app.RequestedThemeVariant;
        var source = Path.Combine(scope.Root, "自动填充测试");
        Directory.CreateDirectory(Path.Combine(source, "appearance", "red"));
        Directory.CreateDirectory(Path.Combine(source, "appearance", "blue"));
        await File.WriteAllBytesAsync(Path.Combine(source, "cover.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "appearance", "preview.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "appearance", "red", "red.png"), [1]);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            void Layout()
            { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, (window.InputHitTest(point) as Visual)!.GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            async Task WaitUntil(Func<bool> predicate)
            {
                for (var i = 0; i < 200 && !predicate(); i++) { await Task.Delay(20); Layout(); }
                Assert.True(predicate(), vm.StatusMessage);
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var input = view.FindControl<TextBox>("CreationSourceInput")!;
            input.Focus(); window.KeyTextInput(source);
            await WaitUntil(() => vm.ModName == "自动填充测试" && !vm.IsBusy);
            Assert.Equal("cover.png", vm.IconPath);
            var option = Assert.Single(vm.Options);
            Assert.Equal("appearance", option.Name); Assert.Equal("appearance/preview.png", option.ImagePath);
            Assert.Equal(2, option.SubOptions.Count);
            Assert.Contains("appearance/red/red.png", vm.JsonPreview);
            var tabs = view.FindControl<TabControl>("CreationTabs")!;
            tabs.SelectedIndex = 1; Layout();

            async Task<FAContentDialog> OpenPicker()
            {
                Click(view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CreationSelectDirectoriesButton"));
                await WaitUntil(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => dialog.IsVisible));
                return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => dialog.IsVisible);
            }
            CheckBox Box(FAContentDialog dialog, string path) => dialog.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => box.DataContext is ModCreationDirectoryViewModel directory && directory.RelativePath == path);
            var dialog = await OpenPicker();
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language); Layout();
                Assert.Equal(UiLocalization.Current["Creator.AddDirectory"], dialog.Title);
                Assert.Equal(UiLocalization.Current["Common.Confirm"], dialog.PrimaryButtonText);
                Assert.Equal(UiLocalization.Current["Common.Cancel"], dialog.CloseButtonText);
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme; window.Width = 960; window.Height = 600; Layout();
                    Assert.True(Box(dialog, "appearance/red").Bounds.Width > 0);
                    Capture(window, theme.ToString(), $"creation-directory-picker-{language}");
                }
            }
            await localization.SetLanguageAsync("zh-CN"); app.RequestedThemeVariant = originalTheme;
            window.Width = 1100; window.Height = 800; Layout();
            Assert.True(Box(dialog, "appearance").IsChecked);
            Assert.False(Box(dialog, "appearance/red").IsChecked);
            Click(Box(dialog, "appearance")); Click(Box(dialog, "appearance/red"));
            // 取消不应改变草稿。
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, dialog.CloseButtonText)));
            await WaitUntil(() => !window.GetVisualDescendants().Contains(dialog));
            Assert.Equal("appearance", option.IncludePaths);
            dialog = await OpenPicker();
            Assert.True(Box(dialog, "appearance").IsChecked);
            Click(Box(dialog, "appearance")); Click(Box(dialog, "appearance/red"));
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, dialog.PrimaryButtonText)));
            await WaitUntil(() => !window.GetVisualDescendants().Contains(dialog));
            Assert.Equal("appearance/red", option.IncludePaths);
            Assert.Equal("appearance", option.Name); Assert.Equal("appearance/preview.png", option.ImagePath);
            dialog = await OpenPicker();
            Assert.True(Box(dialog, "appearance/red").IsChecked);
            Click(Box(dialog, "appearance/red"));
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, dialog.PrimaryButtonText)));
            await WaitUntil(() => !window.GetVisualDescendants().Contains(dialog));
            Assert.Empty(option.IncludePaths); Assert.Contains("\"Include\": []", vm.JsonPreview);

            vm.AddOptionCommand.Execute(null); var added = vm.SelectedOption!;
            var tree = await vm.ReadDirectoriesAsync();
            var red = tree!.Children.Single().Children.Single(directory => directory.Name == "red");
            vm.ApplyDirectories(added, [red]);
            Assert.Equal("red", added.Name); Assert.Equal("appearance/red/red.png", added.ImagePath);
            added.AddSubOptionCommand.Execute(null);
            var sub = Assert.Single(added.SubOptions); vm.ApplyDirectories(sub, [red]);
            Assert.Equal("red", sub.Name); Assert.Equal("appearance/red/red.png", sub.ImagePath);

            var manifest = Path.Combine(source, "manifest.json"); await vm.SaveAsync(manifest);
            var loaded = await scope.Services.GetRequiredService<ModCreationService>().LoadAsync(manifest);
            Assert.Empty(loaded.Manifest.Options![0].Include!);
            Assert.Equal(["appearance/red"], loaded.Manifest.Options[1].Include);
            var preserved = vm.Options[0]; vm.ModName = "自定义标题"; vm.IconPath = "custom.png";
            var other = Path.Combine(scope.Root, "another"); Directory.CreateDirectory(other);
            vm.SourceDirectory = other; await vm.InitializeSourceAsync();
            Assert.Equal("自定义标题", vm.ModName); Assert.Equal("custom.png", vm.IconPath); Assert.Same(preserved, vm.Options[0]);
            await vm.OpenAsync(manifest); await vm.InitializeSourceAsync();
            Assert.False(vm.IsDirty); Assert.Equal("自动填充测试", vm.ModName);
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }
}
