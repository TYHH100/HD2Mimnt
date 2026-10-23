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
    public async Task ModCreationCompression_ShouldSelectLevelsAndImportRealSevenZip()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync(); main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        var app = Avalonia.Application.Current!; var theme = app.RequestedThemeVariant;
        var source = Path.Combine(scope.Root, "新档案7z"); Directory.CreateDirectory(source);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(source, "aaaaaaaaaaaaaaaa.patch_0"))))
        {
            writer.Write(0xF0000011u); writer.Write(1u); writer.Write(1u); writer.Write(new byte[60]);
            writer.Write(new byte[32]); writer.Write(ulong.MaxValue); writer.Write(0xcd4238c6a0c69e32UL);
            writer.Write(184UL); writer.Write(new byte[32]); writer.Write(4u); writer.Write(new byte[20]); writer.Write(new byte[65536]);
        }
        vm.SourceDirectory = source; await vm.InitializeSourceAsync();
        vm.Author = "压缩测试";
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Select(ComboBox box, int index)
            {
                box.Focus(); window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
                window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
                for (var i = 0; i < index; i++) window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Layout();
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            view.FindControl<TabControl>("CreationTabs")!.SelectedItem = view.FindControl<TabItem>("CreationPackagingTab");
            Layout();
            var format = view.FindControl<ComboBox>("CreationArchiveFormat")!;
            var compression = view.FindControl<ComboBox>("CreationCompressionLevel")!;
            Assert.Equal("7z", vm.ArchiveExtension); Assert.Equal(4, compression.SelectedIndex);
            var levels = new[] { ModArchiveCompression.Store, ModArchiveCompression.Fast, ModArchiveCompression.Normal, ModArchiveCompression.High, ModArchiveCompression.Ultra };
            for (var i = 0; i < levels.Length; i++)
            { Select(compression, i); Assert.Equal(levels[i], vm.ArchiveCompression); }
            Select(format, 1); Assert.Equal("zip", vm.ArchiveExtension);
            Select(format, 0); Assert.Equal("7z", vm.ArchiveExtension);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language);
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = variant; window.Width = 960; window.Height = 600; Layout();
                    Assert.Equal(UiLocalization.Current["Creator.CompressionUltra"], ((TextBlock)((ComboBoxItem)compression.SelectedItem!).Content!).Text);
                    Assert.True(format.IsEffectivelyVisible); Assert.True(compression.IsEffectivelyVisible);
                    Assert.True(view.FindControl<Button>("ExportModArchiveButton")!.IsEffectivelyVisible);
                    Capture(window, variant.ToString(), $"creation-compression-{language}");
                }
            }
            var output = Path.Combine(scope.Root, "新档案.7z");
            await vm.ExportAsync(output); Layout();
            Assert.True(File.Exists(output), vm.StatusMessage); Assert.False(vm.IsDirty);
            await main.LibraryViewModel.ImportPathsAsync([output]);
            Assert.Equal(1, main.LibraryViewModel.Import.SuccessCount);
            var imported = Assert.Single(await scope.Services.GetRequiredService<TestDataService>().GetAllModsAsync(), mod => mod.Name == "新档案7z");
            Assert.Equal(ManifestVersion.V1, imported.Manifest!.ManifestVersion);
            Assert.Equal("压缩测试", imported.Author);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, "aaaaaaaaaaaaaaaa.patch_0")),
                await File.ReadAllBytesAsync(Path.Combine(imported.Path, "aaaaaaaaaaaaaaaa.patch_0")));
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = theme; }
    }
}
