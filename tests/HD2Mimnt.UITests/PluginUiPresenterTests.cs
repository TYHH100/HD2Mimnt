using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Xunit;

namespace HD2Mimnt.UITests;

/// <summary>
/// 插件声明式界面新区块的回归：switch / slider 行渲染成真控件，开关拨动即
/// 自动执行区块的动作（当前输入随事件回传），滑条与数字框互相同步并按步进
/// 取整。HD2 Mod Setting GUI 插件的设置页依赖这两个行为。
/// </summary>
public class PluginUiPresenterTests
{
    private static PluginUiSurfaceViewModel Surface(Mock<IPluginService> service, params PluginUiBlock[] blocks)
        => new(
            new PluginUiBundle { PluginId = "test", PluginName = "测试插件" },
            surfaceId: "page",
            title: "测试",
            description: null,
            icon: null,
            initialBlocks: blocks,
            isPage: true,
            pluginService: service.Object);

    private static (Window Window, PluginUiPresenter Presenter) Show(PluginUiSurfaceViewModel surface)
    {
        var presenter = new PluginUiPresenter { Surface = surface };
        var window = new Window { Width = 800, Height = 600, Content = presenter };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return (window, presenter);
    }

    [AvaloniaFact]
    public async Task Switch_TogglingCommitsActionWithValues()
    {
        var service = new Mock<IPluginService>();
        string? arguments = null;
        service.Setup(s => s.ExecutePluginCommandAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("已保存"))
            .Callback<string, string, string, CancellationToken>((_, _, args, _) => arguments = args);

        var surface = Surface(service, new PluginUiBlock
        {
            Type = PluginUiBlockTypes.Switch,
            Id = "glow",
            Title = "辉光",
            Text = "开启后可见。",
            Input = new PluginUiInput { Id = "glow", Value = "false" },
            Actions =
            {
                new PluginUiAction { Command = "apply", Arguments = "{\"key\":\"glow\",\"op\":\"set\"}" }
            },
        });

        var (window, presenter) = Show(surface);
        try
        {
            var toggle = presenter.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            Assert.False(toggle.IsChecked == true);
            Assert.True(presenter.GetVisualDescendants()
                .OfType<TextBlock>().Any(t => t.Text == "开启后可见。"), "名称下的说明文字没有渲染");

            toggle.IsChecked = true;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Dispatcher.UIThread.RunJobs();

            service.Verify(s => s.ExecutePluginCommandAsync(
                "test", "apply", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            using var doc = JsonDocument.Parse(arguments!);
            var values = doc.RootElement.GetProperty("ui").GetProperty("values");
            Assert.Equal("true", values.GetProperty("glow").GetString());
            Assert.Equal("glow", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal("set", doc.RootElement.GetProperty("op").GetString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Slider_SyncsNumberBoxAndSnapsToStep()
    {
        var service = new Mock<IPluginService>();
        var surface = Surface(service, new PluginUiBlock
        {
            Type = PluginUiBlockTypes.Slider,
            Id = "size",
            Title = "大小",
            Input = new PluginUiInput { Id = "size", Value = "0.5", Min = 0.2, Max = 1, Step = 0.05 },
            Actions = { new PluginUiAction { Command = "apply" } },
        });

        var (window, presenter) = Show(surface);
        try
        {
            var slider = presenter.GetVisualDescendants().OfType<Slider>().Single();
            var numberBox = presenter.GetVisualDescendants().OfType<FANumberBox>().Single();
            Assert.Equal(0.2, slider.Minimum);
            Assert.Equal(1, slider.Maximum);
            Assert.Equal(0.5, slider.Value, 6);

            // 拖到步进之间：两个控件落在同一步进上（0.73 → 0.75）
            slider.Value = 0.73;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0.75, slider.Value, 6);
            Assert.Equal(0.75, numberBox.Value, 6);

            // 数字框输入：滑条跟到同一步进
            numberBox.Value = 0.4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0.4, slider.Value, 6);
        }
        finally
        {
            window.Close();
        }
    }
}
