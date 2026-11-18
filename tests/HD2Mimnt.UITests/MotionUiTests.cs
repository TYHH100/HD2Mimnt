using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task InterfaceAnimationsSetting_ShouldToggleMotionStylesAndSkipJelly()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.SettingsViewModel.LoadAsync();

        var host = new Grid();
        host.Classes.Add("motion");
        var probe = new Border { Height = 40 };
        probe.Classes.Add("mod-card");
        host.Children.Add(probe);

        var jellyRoot = new Grid();
        jellyRoot.Children.Add(new Border { Width = 40, Height = 20 });

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { host, jellyRoot } }
        };
        var animator = new PageJellyAnimator(jellyRoot);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.NotNull(probe.Transitions);
            Assert.True(probe.Transitions!.Count >= 1);

            main.SettingsViewModel.EnableAnimations = false;
            await main.ApplyAppearanceAsync();
            host.Classes.Set("motion", false);
            Dispatcher.UIThread.RunJobs();

            Assert.False(UiMotion.IsEnabled);
            Assert.True(probe.Transitions is null || probe.Transitions.Count == 0);
            animator.Start();
            Assert.False(animator.IsActive);

            main.SettingsViewModel.EnableAnimations = true;
            await main.ApplyAppearanceAsync();
            host.Classes.Set("motion", true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(UiMotion.IsEnabled);
            Assert.NotNull(probe.Transitions);
            Assert.True(probe.Transitions!.Count >= 1);
            animator.Start();
            Assert.True(animator.IsActive);
        }
        finally
        {
            animator.Stop();
            main.SettingsViewModel.EnableAnimations = true;
            UiMotion.Apply(true);
            window.Content = null;
            window.Close();
        }
    }
}
