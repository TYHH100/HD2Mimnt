using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task Hd2IrImport_ConfirmationCancellationAndProfileOptions_ShouldUseRealServices()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.LibraryViewModel;
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var mods = scope.Services.GetRequiredService<ModService>();
        var source = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin", "HD2IndependentRig"));
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "HD2IndependentRig", "hero.rigbin"), "rig");
        var inner = Path.Combine(scope.Root, "inner"); Directory.CreateDirectory(inner);
        await File.WriteAllTextAsync(Path.Combine(inner, "0123456789abcdef.patch_0"), "patch");
        // 不同层级 + 不相似名称产生需要确认的组合。
        var middle = Path.Combine(scope.Root, "middle"); Directory.CreateDirectory(middle);
        ZipFile.CreateFromDirectory(inner, Path.Combine(middle, "model.zip"));
        ZipFile.CreateFromDirectory(middle, Path.Combine(source, "resources.zip"));
        var overlay = new ModImportOverlay { DataContext = library };
        var window = new Window { Content = overlay, Width = 900, Height = 760 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            async Task WaitFor(Func<bool> condition)
            {
                for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
                Assert.True(condition()); Layout();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, ((Visual)window.InputHitTest(point)!).GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }

            var cancelled = library.ImportPathsAsync([source]);
            await WaitFor(() => library.Import.HasPendingHd2Ir);
            Assert.Equal(2, library.Import.PendingHd2Ir!.Components.Count);
            library.Import.CancelCommand.Execute(null);
            await cancelled;
            Assert.False(library.Import.HasPendingHd2Ir);
            Assert.Empty(await mods.GetAllModsAsync());

            var import = library.ImportPathsAsync([source]);
            await WaitFor(() => library.Import.HasPendingHd2Ir);
            Capture(window, "Dark", "hd2ir-import-confirmation");
            Click(overlay.FindControl<Button>("ConfirmHd2IrImportButton")!);
            await import;
            Assert.Empty(library.Import.Errors);
            Assert.Equal(1, library.Import.SuccessCount);
            var mod = Assert.Single(await mods.GetAllModsAsync());
            var item = new ModItemViewModel(mod);
            Assert.True(item.IsHd2Ir);
            Assert.Equal(2, item.Hd2IrComponents.Count);
            Assert.NotNull(mod.LogicalMod);
            window.Content = new ModDetailPanel { DataContext = item };
            Layout(); Capture(window, "Dark", "hd2ir-details");

            var profile = (await profiles.GetActiveProfileAsync())!;
            await profiles.AddModToProfileAsync(profile.Id, mod.Id);
            await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
            var row = main.ProfileViewModel.ProfileMods.Single(row => row.ModId == mod.Id);
            Assert.True(row.HasOptions);
            Assert.True(row.SourceInfo!.IsHd2Ir);
            main.ProfileViewModel.OpenModOptionsCommand.Execute(row);
            var options = main.ProfileViewModel.ModOptionsDialog!;
            Assert.Equal(2, options.OptionCards.Count);
            var selector = new ModOptionsSelector { DataContext = options };
            window.Content = selector; Layout();
            var toggle = selector.GetVisualDescendants().OfType<CheckBox>().First();
            Click(toggle);
            bool saved = false;
            for (var i = 0; i < 100 && !saved; i++)
            {
                var snapshot = (await profiles.GetProfileByIdAsync(profile.Id))!;
                saved = ProfileModOptionsState.FromJson(snapshot.Mods.Single().OptionsJson).OptionToggles?[0] == false;
                if (!saved) await Task.Delay(10);
            }
            Assert.True(saved);
            main.ProfileViewModel.CloseModOptionsCommand.Execute(null);
            await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
            main.ProfileViewModel.OpenModOptionsCommand.Execute(main.ProfileViewModel.ProfileMods.Single(row => row.ModId == mod.Id));
            Assert.False(main.ProfileViewModel.ModOptionsDialog!.OptionCards[0].IsEnabled);
        }
        finally
        {
            if (library.Import.IsImporting) { library.Import.CancelCommand.Execute(null); await library.Import.Completion; }
            main.ProfileViewModel.CloseModOptionsCommand.Execute(null);
            window.Content = null; window.Close();
        }
    }
}
