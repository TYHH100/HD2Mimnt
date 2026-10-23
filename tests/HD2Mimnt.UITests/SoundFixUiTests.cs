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
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;
using static HD2Mimnt.Tests.Services.SoundFixFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task SoundFix_ActualDiAndViewScanRepairRestoreLanguageAuthorAndCloseCancellation()
    {
        var reader = new Reader(); reader.Banks[1] = new(Bank((1, new byte[] { 1 }), (2, new byte[] { 2 })));
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(), soundBanks: reader);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var vm = main.SoundFixViewModel!;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync("Game.Path", Path.Combine(scope.Root, "game"));
        var mod = new Mod { Id = Guid.NewGuid(), Name = "HD2Mimnt Test 音效修复", Path = Path.Combine(scope.Root, "mod") };
        var path = WritePatch(mod.Path, resources: [Sound(1, Bank((1, new byte[] { 9, 8, 7 }))), Name(1, "content/audio/weapon")]);
        var original = File.ReadAllBytes(path);
        await scope.Services.GetRequiredService<IModRepository>().AddAsync(mod);
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "音效修复";
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 760 };
        var app = Avalonia.Application.Current!; var previousTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<SoundFixView>().Single();
            Assert.Same(vm, view.DataContext); Assert.True(view.IsEffectivelyVisible);
            void Click(Button button)
            {
                var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                Assert.True(button.IsEnabled); window.MouseDown(position, MouseButton.Left); window.MouseUp(position, MouseButton.Left);
            }
            var scan = view.FindControl<Button>("ScanSoundLibraryButton")!;
            Click(scan); await vm.ScanCommand.ExecutionTask!; Layout();
            Assert.Single(vm.Mods); Assert.True(vm.SelectedMod!.Report.CanRepair);
            Assert.Contains("可修复", vm.SelectedMod.Details);
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code); app.RequestedThemeVariant = theme; Layout();
                Assert.DoesNotContain("SoundFix.", vm.StatusMessage);
                Assert.DoesNotContain("SoundFix.", vm.SelectedMod.Details);
                Assert.True(scan.Bounds.Width > 60);
                Assert.True(view.FindControl<TextBox>("SoundFixDetails")!.Bounds.Width > 200);
                if (code == "en-US") Assert.Contains("Repairable", vm.SelectedMod.Details);
                Capture(window, theme.ToString(), "soundfix-" + code);
            }
            await language.SetLanguageAsync("zh-CN"); Layout();
            Click(view.FindControl<Button>("RepairSoundModButton")!); await vm.RepairCommand.ExecutionTask!; Layout();
            Assert.NotEqual(original, File.ReadAllBytes(path)); Assert.True(vm.SelectedMod!.HasBackup); Assert.False(vm.RepairCommand.CanExecute(null));
            Click(view.FindControl<Button>("RestoreSoundModButton")!); await vm.RestoreCommand.ExecutionTask!; Layout();
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.False(vm.SelectedMod!.HasBackup);
            vm.OpenAuthorCommand.Execute(null); Assert.Contains(SoundFixService.AuthorUrl, scope.WebsiteLauncher.Urls);
            reader.WaitForCancellation = true;
            var pending = vm.ScanCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            await main.CanCloseAsync(); await pending;
            Assert.False(vm.IsBusy); Assert.True(vm.Completion.IsCompleted);
            Assert.Empty(Directory.GetFiles(mod.Path, "*.tmp", SearchOption.AllDirectories));
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = previousTheme; }
    }
}
