using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task HellForgeGameSource_ActualRadiosSwapPathControlsAndSave()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        vm.SelectedCategory = "游戏";
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1040, Height = 800, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var officialRadio = view.FindControl<RadioButton>("OfficialGameSourceRadio")!;
            var hellForgeRadio = view.FindControl<RadioButton>("HellForgeGameSourceRadio")!;
            var officialItem = view.FindControl<FASettingsExpanderItem>("OfficialGamePathItem")!;
            var hellForgeItem = view.FindControl<FASettingsExpanderItem>("HellForgeGamePathItem")!;
            var officialPathInput = view.FindControl<TextBox>("GamePathInput")!;
            var hellForgePathInput = view.FindControl<TextBox>("HellForgePathInput")!;
            Assert.True(officialRadio.IsChecked);
            Assert.True(officialItem.IsEffectivelyVisible);
            Assert.False(hellForgeItem.IsEffectivelyVisible);

            var root = Path.Combine(scope.Root, "HellForge");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "hellforge-install.json"), "{}");
            var game = Path.Combine(root, "game");
            foreach (var name in new[] { "data", "tools", "bin" })
                Directory.CreateDirectory(Path.Combine(game, name));
            File.WriteAllText(Path.Combine(game, "bin", "helldivers2.exe"), string.Empty);
            vm.HellForgePath = root;

            ClickVersionControl(window, hellForgeRadio);
            window.UpdateLayout();

            Assert.False(vm.IsOfficialGameSource);
            Assert.True(vm.IsHellForgeGameSource);
            Assert.False(officialItem.IsEffectivelyVisible);
            Assert.True(hellForgeItem.IsEffectivelyVisible);
            Assert.False(officialPathInput.IsEffectivelyVisible);
            Assert.True(hellForgePathInput.IsEffectivelyVisible);
            Assert.Equal(root, hellForgePathInput.Text);

            ClickVersionControl(window, view.FindControl<Button>("SaveSettingsButton")!);
            await vm.SaveCommand.ExecutionTask!;
            Assert.Contains("设置已保存", vm.StatusMessage);
            Assert.Equal(GameInstallationMode.HellForge, new SettingsService(scope.Root).GetGameInstallationMode());
            Assert.Equal(Path.Combine(root, "game"),
                scope.Services.GetRequiredService<GameDetectionService>().CurrentGameInfo?.Path);
        }
        finally
        {
            window.Close();
        }
    }
}
