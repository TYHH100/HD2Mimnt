using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModVersionSettings_ActualToggleWaitsForConfirmationAndPersistsBothDefaults()
    {
        var confirmation = new RecordingConfirmation { Pending = new() };
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation);
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await vm.LoadAsync(); vm.SelectedCategory = "模组库";
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1040, Height = 850, Content = view };
        try
        {
            window.Show();
            var keep = view.FindControl<ToggleSwitch>("KeepModVersionsToggle")!;
            var duplicates = view.FindControl<ToggleSwitch>("AllowDuplicateImportsToggle")!;
            Assert.False(vm.KeepModVersions); Assert.False(vm.AllowDuplicateImports);
            Assert.False(keep.IsChecked);
            ClickVersionControl(window, keep);
            Assert.True(confirmation.PendingConfirmation); Assert.False(vm.KeepModVersions);
            Assert.False(keep.IsChecked);
            Assert.Contains("磁盘空间", confirmation.Message);
            confirmation.Pending!.SetResult(false);
            await vm.ToggleKeepModVersionsCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.KeepModVersions); Assert.False(keep.IsChecked);
            confirmation.Pending = new();
            ClickVersionControl(window, keep);
            confirmation.Pending.SetResult(true);
            await vm.ToggleKeepModVersionsCommand.ExecutionTask!;
            Assert.True(vm.KeepModVersions);
            Dispatcher.UIThread.RunJobs();
            Assert.True(keep.IsChecked);
            ClickVersionControl(window, duplicates);
            Assert.True(vm.AllowDuplicateImports);
            ClickVersionControl(window, view.FindControl<Button>("SaveSettingsButton")!);
            await vm.SaveCommand.ExecutionTask!;
            Assert.True(settings.GetSetting(SettingKeys.KeepModVersions, false));
            Assert.True(settings.GetSetting(SettingKeys.AllowDuplicateImports, false));
            var language = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await language.SetLanguageAsync(code);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == language["Settings.Library.KeepVersions"]);
                Assert.True(keep.IsEffectivelyVisible);
                Assert.Equal(language["UI.On"], keep.OnContent);
                Assert.Equal(language["UI.Off"], keep.OffContent);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ModVersions_LibraryAndProfileDetailButtonsSwitchAndRefreshActualContent()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation());
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var mods = scope.Services.GetRequiredService<ModService>();
        string Archive(string version)
        {
            var file = Path.Combine(scope.Root, version + ".zip");
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("{\"Name\":\"History\",\"Version\":\"" + version + "\"}");
            using (var writer = new StreamWriter(zip.CreateEntry("0123456789abcdef.patch_0").Open())) writer.Write(version);
            return file;
        }
        var id = await mods.ImportModAsync(Archive("1"));
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        await profiles.InitializeAsync();
        var active = (await profiles.GetActiveProfileAsync())!;
        await profiles.AddModToProfileAsync(active.Id, id);
        await mods.UpdateFromLocalArchiveAsync(id, Archive("2"));
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.LibraryViewModel;
        var profile = main.ProfileViewModel;
        var window = new Window { Width = 480, Height = 1000 };
        try
        {
            var target = Assert.Single(library.FilteredMods);
            library.ShowModDetailCommand.Execute(target);
            await library.LocalVersions.Completion;
            var detail = new ModDetailPanel { DataContext = library.SelectedModDetail };
            window.Content = detail; window.Show();
            var panel = detail.GetVisualDescendants().OfType<ModVersionsPanel>().Single();
            Assert.Same(library.LocalVersions, panel.DataContext);
            Assert.Single(library.LocalVersions.Versions);
            ClickVersionControl(window, panel.FindControl<Button>("SwitchLocalVersionButton")!);
            await library.LocalVersions.SwitchCommand.ExecutionTask!;
            Assert.Equal("1", (await mods.GetModByIdAsync(id))!.Version);
            Assert.Equal("1", library.SelectedModDetail!.Version);
            Assert.Equal("1", Assert.Single(library.FilteredMods).Version);
            await profile.RefreshAfterProfilesChangedAsync();
            var row = Assert.Single(profile.ProfileMods);
            profile.ShowModDetailCommand.Execute(row);
            await profile.LocalVersions.Completion;
            detail = new ModDetailPanel { DataContext = profile.SelectedModDetail }; window.Content = detail;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            panel = detail.GetVisualDescendants().OfType<ModVersionsPanel>().Single();
            Assert.Same(profile.LocalVersions, panel.DataContext);
            ClickVersionControl(window, panel.FindControl<Button>("SwitchLocalVersionButton")!);
            await profile.LocalVersions.SwitchCommand.ExecutionTask!;
            Assert.Equal("2", (await mods.GetModByIdAsync(id))!.Version);
            Assert.Equal("2", Assert.Single(library.FilteredMods).Version);
            Assert.Equal("2", Assert.Single(profile.ProfileMods).ModVersion);
        }
        finally { await main.CanCloseAsync(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void ClickVersionControl(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.True(control.IsEffectivelyVisible); Assert.True(control.Bounds.Width > 0);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
