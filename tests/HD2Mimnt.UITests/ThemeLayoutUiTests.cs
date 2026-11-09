using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ThemeSwitch_ShouldNotReserveHiddenTabStripHeight()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var vm = scope.Services.GetRequiredService<MainWindowViewModel>();
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var profile = window.GetVisualDescendants().OfType<ProfileView>().First();

            AppThemeService.Apply(AppThemeService.Ore);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.InRange(profile.TranslatePoint(new Point(0, 0), window)!.Value.Y, 0, 120);

            AppThemeService.Apply(AppThemeService.Dark);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.InRange(profile.TranslatePoint(new Point(0, 0), window)!.Value.Y, 0, 120);
        }
        finally
        {
            window.Close();
            AppThemeService.Apply(AppThemeService.Dark);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
