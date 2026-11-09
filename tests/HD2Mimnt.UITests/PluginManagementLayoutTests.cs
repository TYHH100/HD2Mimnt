using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Moq;
using Xunit;

namespace HD2Mimnt.UITests;

/// <summary>
/// 插件管理页统计行布局回归。
/// 曾经的缺陷：统计行 Grid 有四列，但「插件 / 总数 / 已启用」三个 TextBlock
/// 漏写 Grid.Column，全部落在第 0 列互相重叠（用户截图实测）。
/// 这里断言四个文本按列排开、互不重叠。
/// </summary>
public class PluginManagementLayoutTests
{
    [AvaloniaFact]
    public async Task StatsRow_TextsDoNotOverlap()
    {
        var service = new Mock<IPluginService>();
        service.Setup(s => s.GetAllPluginsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = "p1", Name = "插件一", Version = "1.0.0", Enabled = true },
                new() { Id = "p2", Name = "插件二", Version = "1.0.0", Enabled = false }
            });
        service.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginPermission>());

        var viewModel = new PluginManagementViewModel(service.Object);
        await viewModel.InitializeAsync();
        Assert.Equal(2, viewModel.TotalPlugins);
        Assert.Equal(1, viewModel.EnabledPlugins);

        var view = new PluginManagementView { DataContext = viewModel };
        var window = new Window { Width = 960, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            var root = (Grid)view.Content!;
            var statsRow = (Grid)root.Children[1];
            var texts = statsRow.Children.OfType<TextBlock>().ToList();
            Assert.Equal(4, texts.Count);

            for (var i = 0; i < texts.Count - 1; i++)
            {
                var left = texts[i].Bounds;
                var right = texts[i + 1].Bounds;
                Assert.True(
                    left.Right <= right.Left + 0.5,
                    $"统计行第 {i} 与第 {i + 1} 个文本重叠：{left} / {right}");
            }
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }
}