using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using HD2Mimnt.UI.Controls;
using Xunit;

namespace HD2Mimnt.UITests;

/// <summary>
/// Markdown 渲染的布局回归测试。
///
/// 曾经的缺陷：列表行用横向 StackPanel 排列「项目符号 + 文本」，
/// 横向容器以无限宽度测量子元素，TextWrapping.Wrap 失效，
/// 长条目被撑成一条 1143px 的行（容器只有 608px），
/// 结果溢出窗口右边界、贴近边缘处与相邻行视觉重叠。
/// 因此这里断言：渲染出的任何文本控件宽度都不得超过可用宽度。
/// </summary>
public class MarkdownLayoutTests
{
    private const double WindowWidth = 560;
    private const double ContentPadding = 16;
    private static readonly double AvailableWidth = WindowWidth - ContentPadding * 2;

    private const string LongListMarkdown = """
        - **模组**：Boot Mod Loader v0.11.0（作者：VeganSquid）
        - **功能**：自动吞噬 2026-10-20 更新后 启动 lua 模组（`-- HD2-Addon：`）以及一段额外的很长的描述文字用来触发换行
        - **机制**：原样运行游戏自身的启动脚本，不修改该脚本
        """;

    [AvaloniaFact]
    public void LongListItem_WrapsWithinAvailableWidth()
    {
        var view = RenderInWindow(LongListMarkdown);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(texts);

        var widest = texts.Max(t => t.Bounds.Width);
        Assert.True(
            widest <= AvailableWidth + 1,
            $"文本宽度 {widest:0.#} 超过可用宽度 {AvailableWidth:0.#}，列表项没有折行");
    }

    [AvaloniaFact]
    public void LongNoticeBlock_WrapsWithinAvailableWidth()
    {
        var surface = new HD2Mimnt.UI.ViewModels.PluginUiSurfaceViewModel(
            new HD2Mimnt.Core.Interfaces.PluginUiBundle { PluginId = "plugin.a", PluginName = "示例插件" },
            "surface",
            "标题",
            null,
            "Plugin",
            new[]
            {
                new HD2Mimnt.PluginSDK.PluginUiBlock
                {
                    Type = HD2Mimnt.PluginSDK.PluginUiBlockTypes.Notice,
                    Tone = HD2Mimnt.PluginSDK.PluginUiTones.Warning,
                    Title = "提示",
                    Text = "这是一段很长的提示文字，用来确认通知区块在窄窗口下会正常折行而不是溢出到窗口右侧之外被裁切。",
                }
            },
            isPage: true,
            pluginService: null);

        var presenter = new PluginUiPresenter { Surface = surface };
        var host = new Window
        {
            Width = WindowWidth,
            Height = 400,
            Content = new ScrollViewer
            {
                Padding = new Thickness(ContentPadding),
                Content = presenter,
            },
        };

        Layout(host);

        var texts = presenter.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(texts);

        var widest = texts.Max(t => t.Bounds.Width);
        Assert.True(
            widest <= AvailableWidth + 1,
            $"文本宽度 {widest:0.#} 超过可用宽度 {AvailableWidth:0.#}，通知区块没有折行");
    }

    private static Control RenderInWindow(string markdown)
    {
        var view = new MarkdownView { Markdown = markdown };
        var host = new Window
        {
            Width = WindowWidth,
            Height = 400,
            Background = new SolidColorBrush(Colors.Black),
            Content = new ScrollViewer
            {
                Padding = new Thickness(ContentPadding),
                Content = view,
            },
        };

        Layout(host);
        return view;
    }

    private static void Layout(Window window)
    {
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }
}
