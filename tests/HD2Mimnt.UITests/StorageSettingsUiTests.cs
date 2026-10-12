using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Storage;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task StorageSettings_ShouldUseActualDiBindingsAndSaveIndependently()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        var storage = scope.Services.GetRequiredService<StorageSettingsService>();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await vm.LoadAsync();
        vm.SelectedCategory = "存储位置";
        vm.GamePath = "invalid-game-path";
        var window = new Window { Width = 960, Height = 600, Content = new SettingsView { DataContext = vm } };
        var app = Avalonia.Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            var view = (SettingsView)window.Content;
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code);
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                foreach (var name in new[] { "AppDataPathInput", "ModsStoragePathInput", "TempCachePathInput" })
                {
                    var input = view.FindControl<TextBox>(name)!;
                    Assert.True(input.IsEffectivelyVisible);
                    Assert.True(input.Bounds.Width >= 180);
                    Assert.False(string.IsNullOrWhiteSpace(input.Text));
                }
                Capture(window, theme.ToString(), "storage-" + code);
            }
            await language.SetLanguageAsync("zh-CN");
            var button = view.FindControl<Button>("SaveSettingsButton")!;
            button.Command!.Execute(button.CommandParameter);
            await vm.SaveCommand.ExecutionTask!;
            Assert.False(storage.HasPendingMigration);
            Assert.False(vm.StorageRestartRequired);
            Assert.Contains("未更改", vm.StatusMessage);
            var unchanged = new StorageLocationStore(scope.Root).State;
            Assert.Null(unchanged.Pending);
            Assert.Null(unchanged.MigrationId);

            var target = Path.Combine(scope.Root, "custom-library");
            view.FindControl<TextBox>("ModsStoragePathInput")!.Text = target;
            vm.DownloadParallelSegments = 0; // 存储页不受游戏/下载设置校验影响。
            Dispatcher.UIThread.RunJobs();
            button.Command!.Execute(button.CommandParameter);
            await vm.SaveCommand.ExecutionTask!;
            Assert.Equal(target, storage.Selected.ModsPath);
            Assert.Equal(target, new StorageLocationStore(scope.Root).State.Pending!.ModsPath);
            Assert.True(vm.StorageRestartRequired);
            Assert.Contains("重启", vm.StatusMessage);
            Assert.NotEqual(target, storage.Current.ModsPath);
            Assert.False(Directory.Exists(target));
            await vm.LoadAsync();
            Assert.Equal(target, vm.ModsStoragePath);
            await vm.ResetCommand.ExecuteAsync(null);
            Assert.Equal(storage.Defaults.ModsPath, vm.ModsStoragePath);
            Assert.Equal(target, storage.Selected.ModsPath); // 重置编辑内容后需保存。
            button.Command!.Execute(button.CommandParameter);
            await vm.SaveCommand.ExecutionTask!;
            Assert.False(storage.HasPendingMigration);
            Assert.False(vm.StorageRestartRequired);
            Assert.Contains("未更改", vm.StatusMessage);
            Assert.Null(new StorageLocationStore(scope.Root).State.Pending);
            Assert.False(Directory.Exists(target));
        }
        finally { window.Close(); app.RequestedThemeVariant = previousTheme; }
    }
}
