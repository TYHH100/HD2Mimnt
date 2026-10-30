using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Core;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task VersionDisplays_ShouldFollowSingleBuildVersion()
    {
        await using var scope = await UiScope.CreateAsync();
        Assert.NotEmpty(AppInfo.Version);
        Assert.DoesNotContain("+", AppInfo.Version);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var badge = window.FindControl<Border>("VersionBadge")!;
            Assert.Equal($"v{AppInfo.Version}", badge.GetVisualDescendants().OfType<TextBlock>().Single().Text);

            main.MoreViewModel.SelectedCategory = "关于";
            main.SelectedTabIndex = 3;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var aboutLines = window.GetVisualDescendants().OfType<MoreView>().Single()
                .GetVisualDescendants().OfType<TextBlock>()
                .Select(text => text.Text ?? string.Empty)
                .Where(text => text.Contains("Helldivers 2", StringComparison.Ordinal))
                .ToArray();
            Assert.Contains(aboutLines, text => text.Contains(AppInfo.Version, StringComparison.Ordinal));
        }
        finally { window.Content = null; window.Close(); }
    }
}
