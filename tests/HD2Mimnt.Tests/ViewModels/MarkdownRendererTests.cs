using Avalonia.Controls;
using HD2Mimnt.UI.Controls;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

/// <summary>
/// Markdown 渲染器的结构校验：插件与 AI 的输出默认按 Markdown 展示，
/// 这里确保常见块（标题、列表、代码块、表格、引用）都能生成控件而不是抛异常。
/// </summary>
public class MarkdownRendererTests
{
    [Fact]
    public void Render_CommonBlocks_ProducesControls()
    {
        const string markdown = """
        # 标题一

        普通段落，包含 **粗体**、*斜体*、~~删除线~~ 与 `行内代码`，以及 [链接](https://example.com)。

        > 引用内容

        - 第一项
        - 第二项
          - 嵌套项

        1. 有序一
        2. 有序二

        ```csharp
        var x = 1;
        ```

        | 列 A | 列 B |
        |---|---|---:|
        | 1 | 2 |

        ---
        """;

        var control = MarkdownRenderer.Render(markdown);

        var panel = Assert.IsType<StackPanel>(control);
        Assert.True(panel.Children.Count >= 7, $"实际块数量：{panel.Children.Count}");
    }

    [Fact]
    public void Render_EmptyText_ReturnsEmptyPanel()
    {
        var panel = Assert.IsType<StackPanel>(MarkdownRenderer.Render(null));
        Assert.Empty(panel.Children);
    }
}
