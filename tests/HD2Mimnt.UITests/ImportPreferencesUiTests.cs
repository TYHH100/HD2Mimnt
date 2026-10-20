using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HD2Mimnt.UI;
using Avalonia.Headless.XUnit;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task SavedAutoAddPreference_WindowDrop_ShouldRetainMembershipAfterProfileRefresh()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var settings = main.SettingsViewModel;
        settings.SelectedCategory = "配置文件";
        settings.AutoAddImportedMods = true;
        await settings.SaveCommand.ExecuteAsync(null);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var active = (await profiles.GetActiveProfileAsync())!;
        var oldCount = active.Mods.Count;
        var source = Path.Combine(scope.Root, "drop-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "content");
        var importPath = Environment.GetEnvironmentVariable("HD2MIMNT_TEST_IMPORT_ARCHIVE") ?? source;
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            window.UpdateLayout();
            IStorageItem? file = Directory.Exists(importPath)
                ? await window.StorageProvider.TryGetFolderFromPathAsync(importPath)
                : await window.StorageProvider.TryGetFileFromPathAsync(importPath);
            Assert.NotNull(file);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, new Point(640, 400), KeyModifiers.None));
            await main.LibraryViewModel.ImportCompletion;
            Dispatcher.UIThread.RunJobs();
            await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
            var saved = (await profiles.GetActiveProfileAsync())!;
            Assert.Empty(main.LibraryViewModel.Import.Errors);
            Assert.Equal(oldCount + 1, saved.Mods.Count);
            Assert.Equal(oldCount + 1, main.ProfileViewModel.ProfileMods.Count);
            Assert.True(saved.Mods.Single(m => !active.Mods.Any(old => old.ModId == m.ModId)).Enabled);
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImportPreferences_ShouldPersistAndApplyThroughRealServices()
    {
        await using var scope = await UiScope.CreateAsync();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        // 本用例反复导入同一内容以验证导入偏好，显式允许重复，避免测到重复拦截。
        await settings.SetSettingAsync(SettingKeys.AllowDuplicateImports, true);
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var library = scope.Services.GetRequiredService<MainWindowViewModel>().LibraryViewModel;
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var source = Path.Combine(scope.Root, "import-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "content");
        await vm.LoadAsync();
        Assert.False(vm.ImportAutoClose);
        Assert.False(vm.AutoAddImportedMods);

        // 默认保留结果；无活动配置也可正常导入。
        await library.ImportPathsAsync([source]);
        Assert.True(library.Import.IsOpen);
        Assert.Equal(1, library.Import.SuccessCount);

        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            vm.GamePath = "invalid-game-path";
            vm.SelectedCategory = "模组库";
            window.UpdateLayout();
            view.FindControl<ToggleSwitch>("ImportAutoCloseToggle")!.IsChecked = true;
            await vm.SaveCommand.ExecuteAsync(null);
            vm.SelectedCategory = "配置文件";
            window.UpdateLayout();
            view.FindControl<ToggleSwitch>("AutoAddImportedModsToggle")!.IsChecked = true;
            await vm.SaveCommand.ExecuteAsync(null);
            await vm.LoadAsync();
            Assert.True(vm.ImportAutoClose);
            Assert.True(vm.AutoAddImportedMods);
            var reopened = new SettingsService(scope.Root);
            Assert.True(reopened.GetSetting(SettingKeys.ImportAutoClose, false));
            Assert.True(reopened.GetSetting(SettingKeys.ProfilesAutoAddImportedMods, false));

            // 已打开的导入器读取最新设置；无活动配置时仍成功。
            await library.ImportPathsAsync([source]);
            Assert.False(library.Import.IsOpen);
            Assert.Equal(1, library.Import.SuccessCount);

            var activeId = await profiles.CreateProfileAsync("active");
            var otherId = await profiles.CreateProfileAsync("other");
            await profiles.SetActiveProfileAsync(activeId);
            await library.ImportPathsAsync([source]);
            Assert.False(library.Import.IsOpen);
            Assert.True(library.Import.HasResult);
            var active = (await profiles.GetProfileByIdAsync(activeId))!;
            Assert.True(Assert.Single(active.Mods).Enabled);
            Assert.Empty((await profiles.GetProfileByIdAsync(otherId))!.Mods);

            await library.ImportPathsAsync([source, Path.Combine(scope.Root, "missing.zip")]);
            Assert.True(library.Import.IsOpen);
            Assert.Equal(1, library.Import.SuccessCount);
            Assert.Equal(1, library.Import.FailedCount);
            Assert.Equal(2, (await profiles.GetProfileByIdAsync(activeId))!.Mods.Count);

            await vm.ResetCommand.ExecuteAsync(null);
            Assert.True(vm.ImportAutoClose); // 配置文件标签不能重置模组库的导入偏好。
            Assert.False(vm.AutoAddImportedMods);
            vm.SelectedCategory = "模组库";
            await vm.ResetCommand.ExecuteAsync(null);
            Assert.False(vm.ImportAutoClose);
            await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "new-content-after-reset");
            await library.ImportPathsAsync([source]);
            Assert.True(library.Import.IsOpen);
            Assert.Equal(1, library.Import.SuccessCount);
            Assert.Equal(2, (await profiles.GetProfileByIdAsync(activeId))!.Mods.Count);
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }
}
