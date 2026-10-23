using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ProfileDeploymentStatus_ShouldBeIndependentOfEnabledStateAndClearAfterCleanup()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.ProfileViewModel;
        var mod = vm.ProfileMods.First(row => !row.IsSeparator);
        var window = new Window { Content = new ProfileView { DataContext = vm }, Width = 1200, Height = 950 };
        try
        {
            window.Show();
            void AssertStatus(string expected)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var card = window.GetVisualDescendants().OfType<Border>().Single(border =>
                    border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
                var labels = card.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
                Assert.Contains(expected, labels);
                Assert.DoesNotContain(expected == "已部署" ? "未部署" : "已部署", labels);
                Assert.DoesNotContain("已启用", labels);
                Assert.DoesNotContain("已停用", labels);
            }

            mod.Enabled = true;
            vm.ApplyDeploymentState(new HashSet<Guid>());
            AssertStatus("未部署");
            vm.ApplyDeploymentState(new HashSet<Guid> { mod.ModId });
            AssertStatus("已部署");
            mod.Enabled = false;
            AssertStatus("已部署");
            vm.ApplyDeploymentState(new HashSet<Guid>());
            AssertStatus("未部署");
        }
        finally { window.Close(); }
    }
}
