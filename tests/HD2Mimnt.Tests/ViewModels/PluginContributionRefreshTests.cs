using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

/// <summary>
/// 插件贡献刷新的回归测试。
///
/// 两个真实缺陷的防线：
/// 1. 停用/卸载插件后没有通知主窗口，导航项与菜单入口残留；
/// 2. 模组库工具栏刷新被并发调用时，“清空 → 追加”交错，同一入口出现两份。
/// </summary>
public class PluginContributionRefreshTests
{
    [Fact]
    public async Task RefreshPluginCommandsAsync_ConcurrentCalls_KeepSingleEntry()
    {
        var menu = new PluginMenuContribution
        {
            Id = "toolbar-quick-summary",
            Placement = PluginCommandPlacements.LibraryToolbar,
            Label = "快速 AI 摘要",
            Dialog = "quick-summary"
        };

        var bundle = new PluginUiBundle
        {
            PluginId = "plugin.a",
            PluginName = "示例插件",
            Ui = new PluginUiContributions { Menus = { menu } }
        };

        var mock = new Mock<IPluginService>();
        mock.Setup(s => s.GetContributedCommandsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(15);
                return new List<PluginCommandContribution>();
            });
        mock.Setup(s => s.GetUiContributionsAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(15);
                return new List<PluginUiBundle> { bundle };
            });

        var viewModel = new LibraryViewModel(
            null!, null!, null!, null!, null!, null!, null!, pluginService: mock.Object);

        // 右键菜单每次弹出都会刷新，启动阶段也可能并行触发
        await Task.WhenAll(
            viewModel.RefreshPluginCommandsAsync(),
            viewModel.RefreshPluginCommandsAsync());

        Assert.Single(viewModel.LibraryPluginActions);
        Assert.Equal("快速 AI 摘要", viewModel.LibraryPluginActions[0].DisplayName);
    }

    [Fact]
    public async Task DisablePlugin_NotifiesContributionsChanged()
    {
        var mock = new Mock<IPluginService>();
        mock.Setup(s => s.GetAllPluginsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = "plugin.a", Name = "示例插件", Version = "1.0.0", Enabled = true }
            });
        mock.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HD2Mimnt.Core.Entities.PluginPermission>());
        mock.Setup(s => s.GetUiContributionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginUiBundle>());
        mock.Setup(s => s.DisablePluginAsync("plugin.a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("已停用"));

        var viewModel = new PluginManagementViewModel(mock.Object);
        await viewModel.InitializeAsync();

        var raised = 0;
        viewModel.ContributionsChanged += (_, _) => raised++;

        await viewModel.DisablePluginCommand.ExecuteAsync(viewModel.InstalledPlugins[0]);

        Assert.True(raised > 0, "停用插件后必须通知主窗口刷新界面贡献，否则导航项不会消失");
    }

    [Fact]
    public async Task EnablePlugin_NotifiesContributionsChanged()
    {
        var mock = new Mock<IPluginService>();
        mock.Setup(s => s.GetAllPluginsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = "plugin.a", Name = "示例插件", Version = "1.0.0", Enabled = false }
            });
        mock.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HD2Mimnt.Core.Entities.PluginPermission>());
        mock.Setup(s => s.GetUiContributionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginUiBundle>());
        mock.Setup(s => s.EnablePluginAsync("plugin.a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("已启用"));

        var viewModel = new PluginManagementViewModel(mock.Object);
        await viewModel.InitializeAsync();

        var raised = 0;
        viewModel.ContributionsChanged += (_, _) => raised++;

        await viewModel.EnablePluginCommand.ExecuteAsync(viewModel.InstalledPlugins[0]);

        Assert.True(raised > 0, "启用插件后必须通知主窗口，否则新页面不会出现在导航栏");
    }
}
