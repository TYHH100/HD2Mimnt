using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
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
    public async Task ModCreationFormat_ShouldSwitchKeepDraftAndExportLegacyDirectories()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        var app = Avalonia.Application.Current!; var originalTheme = app.RequestedThemeVariant;
        var source = Path.Combine(scope.Root, "格式测试");
        Directory.CreateDirectory(Path.Combine(source, "red"));
        Directory.CreateDirectory(Path.Combine(source, "blue"));
        using (var writer = new BinaryWriter(File.Create(Path.Combine(source, "red", "aaaaaaaaaaaaaaaa.patch_0"))))
        {
            writer.Write(0xF0000011u); writer.Write(1u); writer.Write(1u); writer.Write(new byte[60]);
            writer.Write(new byte[32]); writer.Write(ulong.MaxValue); writer.Write(0xcd4238c6a0c69e32UL);
            writer.Write(184UL); writer.Write(new byte[32]); writer.Write(4u); writer.Write(new byte[20]); writer.Write(new byte[4]);
        }
        vm.SourceDirectory = source; await vm.InitializeSourceAsync();
        var v1 = vm.Options.First(); v1.Name = "自定义选项";
        v1.AddSubOptionCommand.Execute(null); var sub = Assert.Single(v1.SubOptions); sub.Name = "子选项";
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
            async Task WaitUntil(Func<bool> predicate)
            {
                for (var i = 0; i < 200 && !predicate(); i++) { await Task.Delay(20); Layout(); }
                Assert.True(predicate(), vm.StatusMessage);
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var newFormat = view.FindControl<RadioButton>("CreationV1Format")!;
            var oldFormat = view.FindControl<RadioButton>("CreationLegacyFormat")!;
            Assert.True(newFormat.IsChecked); Assert.False(oldFormat.IsChecked);
            var guid = JsonNode.Parse(vm.JsonPreview)!["Guid"]!.GetValue<string>();
            Click(oldFormat);
            Assert.True(vm.IsLegacyManifest); Assert.False(newFormat.IsChecked);
            Assert.Equal(2, vm.LegacyOptions.Count); Assert.DoesNotContain("自定义选项", vm.LegacyOptions);
            var legacy = JsonNode.Parse(vm.JsonPreview)!;
            Assert.Null(legacy["Version"]); Assert.Equal(guid, legacy["Guid"]!.GetValue<string>());
            Assert.All(legacy["Options"]!.AsArray(), value => Assert.False(string.IsNullOrWhiteSpace(value!.GetValue<string>())));
            view.FindControl<TabControl>("CreationTabs")!.SelectedIndex = 1; Layout();
            Assert.False(view.FindControl<Button>("AddCreationOptionButton")!.IsEffectivelyVisible);
            vm.SelectedLegacyOption = "blue"; vm.RemoveLegacyOptionCommand.Execute(null);
            Click(view.FindControl<Button>("AddCreationLegacyOptionButton")!);
            await WaitUntil(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => dialog.IsVisible));
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => dialog.IsVisible);
            Click(dialog.GetVisualDescendants().OfType<CheckBox>().Single(box =>
                box.DataContext is ModCreationDirectoryViewModel directory && directory.RelativePath == "blue"));
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, dialog.PrimaryButtonText)));
            await WaitUntil(() => !window.GetVisualDescendants().Contains(dialog));
            Assert.Equal(2, vm.LegacyOptions.Count);
            Click(newFormat);
            Assert.True(vm.IsV1Manifest); Assert.Same(v1, vm.Options.First()); Assert.Same(sub, v1.SubOptions.Single());
            Assert.Equal("自定义选项", v1.Name); Assert.Contains("SubOptions", vm.JsonPreview);
            Click(oldFormat);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language);
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme; window.Width = 960; window.Height = 600; Layout();
                    Assert.Equal(UiLocalization.Current["Creator.FormatV1"], newFormat.Content);
                    Assert.Equal(UiLocalization.Current["Creator.FormatLegacy"], oldFormat.Content);
                    Assert.True(oldFormat.IsEffectivelyVisible); Assert.True(oldFormat.Bounds.Width > 80);
                    Assert.True(view.FindControl<ListBox>("CreationLegacyOptionList")!.Bounds.Height > 0);
                    Capture(window, theme.ToString(), $"creation-format-{language}");
                }
            }
            var manifest = Path.Combine(source, "manifest.json"); await vm.SaveAsync(manifest);
            Assert.False(vm.IsDirty); await vm.OpenAsync(manifest); Layout();
            Assert.True(vm.IsLegacyManifest); Assert.False(vm.IsDirty);
            Assert.Equal(2, vm.LegacyOptions.Count); Assert.Equal(guid, JsonNode.Parse(vm.JsonPreview)!["Guid"]!.GetValue<string>());
            Assert.Equal("7z", vm.ArchiveExtension);
            Assert.Equal(ModArchiveCompression.Ultra, vm.ArchiveCompression);
            var zip = Path.Combine(scope.Root, "legacy.7z"); await vm.ExportAsync(zip);
            Assert.True(File.Exists(zip), vm.StatusMessage);
            await main.LibraryViewModel.ImportPathsAsync([zip]);
            Assert.Equal(1, main.LibraryViewModel.Import.SuccessCount);
            var imported = Assert.Single((await scope.Services.GetRequiredService<TestDataService>().GetAllModsAsync())
                .Where(mod => mod.Name == "格式测试"));
            Assert.Equal(ManifestVersion.Legacy, imported.Manifest!.ManifestVersion);
            Assert.Equal(["red", "blue"], imported.Manifest.LegacyOptions);
            vm.IsDirty = false; vm.NewCommand.Execute(null); await vm.NewCommand.ExecutionTask!;
            Assert.True(vm.IsV1Manifest); Assert.Empty(vm.LegacyOptions); Assert.False(vm.IsDirty);
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }
}
