using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ManagerMigration_ShouldOpenFromToolsAndTransferInBothDirectionsUsingActualButtons()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = Assert.IsType<ManagerMigrationViewModel>(main.ManagerMigrationViewModel);
        var localization = scope.Services.GetRequiredService<ILocalizationService>();
        main.NavigateToToolsCommand.Execute(null);
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView();
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.True(control.IsEffectivelyVisible);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            var tools = window.GetVisualDescendants().OfType<MoreView>().Single();
            var categories = tools.GetVisualDescendants().OfType<ListBox>().First();
            Click(Assert.IsType<ListBoxItem>(categories.ContainerFromIndex(Array.IndexOf(main.MoreViewModel.Categories, "管理器迁移"))));
            Assert.True(main.MoreViewModel.IsMigrationVisible);
            var view = tools.GetVisualDescendants().OfType<ManagerMigrationView>().Single();
            var selector = view.FindControl<ComboBox>("MigrationManagerSelector")!;
            Assert.Equal(3, selector.ItemCount);
            Assert.Equal(new[] { "Helldivers2ModManager", "Echelon", "HD2Arsenal" }, vm.Managers.Select(choice => choice.Name));

            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.Width = 960; window.Height = 600;
                await localization.SetLanguageAsync(code);
                app.RequestedThemeVariant = theme;
                Layout();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == localization["Migration.Title"]);
                Assert.Contains(categories.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == localization["Migration.Title"]);
                Assert.Equal(localization["Migration.ExportDirection"], view.FindControl<RadioButton>("MigrationExportDirection")!.Content);
                Capture(window, theme.ToString(), "migration-" + code);
            }
            window.Width = 1280; window.Height = 800;
            await localization.SetLanguageAsync("zh-CN");
            Click(view.FindControl<RadioButton>("MigrationExportDirection")!);
            Assert.True(vm.IsExport);
            for (var index = 0; index < vm.Managers.Length; index++)
            {
                selector.Focus();
                window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
                window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
                for (var move = 0; move < index; move++) window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Layout();
                Assert.Same(vm.Managers[index], vm.SelectedManager);
                view.FindControl<TextBox>("MigrationDataPath")!.Text = Path.Combine(scope.Root, "export", vm.SelectedManager!.Kind.ToString());
                view.FindControl<TextBox>("MigrationInstallationPath")!.Text = Path.Combine(scope.Root, "install");
                view.FindControl<TextBox>("MigrationModsPath")!.Text = "";
                Layout();
                Click(view.FindControl<Button>("MigrationRunButton")!);
                await vm.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                Layout();
                Assert.StartsWith("迁移完成：3 个模组", vm.StatusMessage);
                Assert.False(vm.IsBusy);
                Assert.Equal(vm.DataPath, scope.Services.GetRequiredService<SettingsService>().GetSetting("Migration." + vm.SelectedManager.Kind + ".DataPath", ""));
            }
            Click(view.FindControl<RadioButton>("MigrationImportDirection")!);
            Assert.False(vm.IsExport);
            Click(view.FindControl<Button>("MigrationPreviewButton")!);
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("预览完成，可开始迁移。", vm.StatusMessage);
            Click(view.FindControl<Button>("MigrationRunButton")!);
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("迁移完成：3 个模组", vm.StatusMessage);
            Assert.False(vm.IsBusy);
            Assert.Equal(6, main.LibraryViewModel.FilteredMods.Count);
        }
        finally { await vm.CancelAndWaitAsync(); window.Close(); app.RequestedThemeVariant = originalTheme; }
    }
}
