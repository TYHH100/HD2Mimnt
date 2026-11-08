using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.Controls;

/// <summary>
/// 轻量 Markdown 渲染器：把插件/AI 返回的 Markdown 文本转成 Avalonia 控件。
///
/// 为什么自己实现而不引第三方库：
/// - 插件结果窗口需要在离线环境下稳定工作，不额外增加 NuGet 依赖；
/// - 样式要跟随管理器当前主题（皮肤切换后颜色一致）；
/// - 只需要常见子集（标题、段落、列表、代码块、引用、表格、分隔线、行内样式和链接）。
///
/// 不支持原始 HTML（按普通文本展示），避免插件借 Markdown 注入任意内容。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Microsoft YaHei UI,monospace");
    private static readonly IBrowserLauncher Browser = new SystemBrowserLauncher();

    /// <summary>渲染整段 Markdown。</summary>
    public static Control Render(string? markdown, double baseFontSize = 13)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return panel;
        }

        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            // ``` 代码块
            if (TryReadFence(line, out var language))
            {
                index++;
                var code = new StringBuilder();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                if (index < lines.Length) index++; // 跳过结束围栏

                panel.Children.Add(BuildCodeBlock(code.ToString().TrimEnd('\n'), language, baseFontSize));
                continue;
            }

            // 分隔线
            if (IsDivider(line))
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 6),
                    Background = Resolve("BorderBrush", Color.FromArgb(60, 128, 128, 128)),
                });
                index++;
                continue;
            }

            // 标题
            if (TryReadHeading(line, out var level, out var headingText))
            {
                var heading = new SelectableTextBlock
                {
                    FontSize = level switch
                    {
                        1 => baseFontSize + 11,
                        2 => baseFontSize + 7,
                        3 => baseFontSize + 4,
                        4 => baseFontSize + 2,
                        _ => baseFontSize + 1,
                    },
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = baseFontSize + 12,
                    Margin = new Thickness(0, level <= 2 ? 6 : 2, 0, 0),
                    Foreground = Resolve("TextPrimaryBrush", Colors.White),
                };
                AppendInlines(heading.Inlines!, headingText, heading.FontSize, baseFontSize);
                panel.Children.Add(heading);
                index++;
                continue;
            }

            // 引用
            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    var content = lines[index].TrimStart()[1..];
                    if (content.StartsWith(' ')) content = content[1..];
                    quote.AppendLine(content);
                    index++;
                }

                var quoteText = new SelectableTextBlock
                {
                    FontSize = baseFontSize,
                    LineHeight = baseFontSize + 8,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Resolve("TextSecondaryBrush", Colors.LightGray),
                };
                AppendInlines(quoteText.Inlines!, quote.ToString().TrimEnd(), baseFontSize, baseFontSize);

                panel.Children.Add(new Border
                {
                    BorderBrush = Resolve("AccentBrush", Color.FromArgb(255, 120, 170, 255)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 0, 4),
                    Child = quoteText,
                });
                continue;
            }

            // 表格
            if (IsTableHeader(lines, index))
            {
                index = BuildTable(lines, index, panel, baseFontSize);
                continue;
            }

            // 列表
            if (TryReadListMarker(line, out _, out _, out _, out _))
            {
                var items = new List<ListEntry>();
                while (index < lines.Length)
                {
                    var candidate = lines[index];
                    if (string.IsNullOrWhiteSpace(candidate)) break;

                    if (TryReadListMarker(candidate, out var indent, out var ordered, out var number, out var text))
                    {
                        var isTask = TryReadTaskState(text, out var isChecked, out var taskText);
                        items.Add(new ListEntry
                        {
                            Level = Math.Max(0, indent / 2),
                            Ordered = ordered,
                            Number = number,
                            Text = isTask ? taskText : text,
                            HasCheckbox = isTask,
                            Checked = isChecked,
                        });
                        index++;
                        continue;
                    }

                    // 列表项的续行：缩进且不是新的块
                    if (char.IsWhiteSpace(candidate[0]) && items.Count > 0)
                    {
                        items[^1].Text += " " + candidate.Trim();
                        index++;
                        continue;
                    }

                    break;
                }

                panel.Children.Add(BuildList(items, baseFontSize));
                continue;
            }

            // 段落：合并到空行或下一个块起始为止
            var paragraph = new StringBuilder();
            while (index < lines.Length)
            {
                var candidate = lines[index];
                if (string.IsNullOrWhiteSpace(candidate)) break;
                if (paragraph.Length > 0 && IsBlockStart(lines, index)) break;

                var hardBreak = candidate.EndsWith("  ", StringComparison.Ordinal);
                paragraph.Append(candidate.TrimEnd());
                paragraph.Append(hardBreak ? "\n" : " ");
                index++;
            }

            var paragraphText = new SelectableTextBlock
            {
                FontSize = baseFontSize,
                LineHeight = baseFontSize + 8,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Resolve("TextPrimaryBrush", Colors.White),
            };
            AppendInlines(paragraphText.Inlines!, paragraph.ToString().TrimEnd(), baseFontSize, baseFontSize);
            panel.Children.Add(paragraphText);
        }

        return panel;
    }

    // ── 块级构建 ────────────────────────────────────────────────────────────

    private static Control BuildCodeBlock(string code, string? language, double baseFontSize)
    {
        var content = new SelectableTextBlock
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = baseFontSize - 1,
            LineHeight = baseFontSize + 5,
            Foreground = Resolve("TextPrimaryBrush", Colors.White),
            TextWrapping = TextWrapping.NoWrap,
        };

        var body = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(language))
        {
            body.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = baseFontSize - 2,
                Foreground = Resolve("TextMutedBrush", Colors.Gray),
            });
        }
        body.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        });

        return new Border
        {
            Background = Resolve("Surface2Brush", Color.FromArgb(255, 32, 34, 40)),
            BorderBrush = Resolve("BorderBrush", Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = body,
        };
    }

    private static Control BuildList(List<ListEntry> entries, double baseFontSize)
    {
        var panel = new StackPanel { Spacing = 4 };
        var counters = new int[8];

        foreach (var entry in entries)
        {
            var level = Math.Clamp(entry.Level, 0, counters.Length - 1);
            for (var i = level + 1; i < counters.Length; i++) counters[i] = 0;

            string marker;
            if (entry.HasCheckbox)
            {
                marker = entry.Checked ? "☑" : "☐";
            }
            else if (entry.Ordered)
            {
                counters[level]++;
                marker = $"{Math.Max(entry.Number, counters[level])}.";
            }
            else
            {
                marker = level == 0 ? "•" : "◦";
            }

            // 必须用 Grid 而不是横向 StackPanel：横向 StackPanel 以无限宽度测量子元素，
            // TextWrapping.Wrap 不会生效，长条目会被撑成一条超宽行并溢出右侧边界。
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(entry.Level * 16, 0, 0, 0),
            };

            var markerBlock = new TextBlock
            {
                Text = marker,
                FontSize = baseFontSize,
                // 与正文同高，首行基线才会对齐；否则符号会比文字高出一截
                LineHeight = baseFontSize + 8,
                MinWidth = entry.Ordered ? 18 : 12,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = Resolve("TextSecondaryBrush", Colors.LightGray),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(markerBlock, 0);
            row.Children.Add(markerBlock);

            var text = new SelectableTextBlock
            {
                FontSize = baseFontSize,
                LineHeight = baseFontSize + 8,
                TextWrapping = TextWrapping.Wrap,
                Foreground = entry.HasCheckbox && entry.Checked
                    ? Resolve("TextMutedBrush", Colors.Gray)
                    : Resolve("TextPrimaryBrush", Colors.White),
                TextDecorations = entry.HasCheckbox && entry.Checked ? TextDecorations.Strikethrough : null,
            };
            Grid.SetColumn(text, 1);
            AppendInlines(text.Inlines!, entry.Text, baseFontSize, baseFontSize);
            row.Children.Add(text);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static int BuildTable(string[] lines, int index, StackPanel panel, double baseFontSize)
    {
        var headers = SplitTableRow(lines[index]);
        var alignments = ParseAlignments(lines[index + 1], headers.Count);
        var rows = new List<List<string>>();
        var cursor = index + 2;
        while (cursor < lines.Length && !string.IsNullOrWhiteSpace(lines[cursor]) && lines[cursor].Contains('|'))
        {
            rows.Add(SplitTableRow(lines[cursor]));
            cursor++;
        }

        var columns = Math.Max(1, headers.Count);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions() };
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }
        for (var i = 0; i <= rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var column = 0; column < columns; column++)
        {
            var headerCell = BuildTableCell(GetCell(headers, column), baseFontSize, isHeader: true, alignments.ElementAtOrDefault(column));
            Grid.SetColumn(headerCell, column);
            Grid.SetRow(headerCell, 0);
            grid.Children.Add(headerCell);
        }

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var cell = BuildTableCell(GetCell(rows[row], column), baseFontSize, isHeader: false, alignments.ElementAtOrDefault(column));
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row + 1);
                grid.Children.Add(cell);
            }
        }

        panel.Children.Add(new Border
        {
            BorderBrush = Resolve("BorderBrush", Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = grid,
            },
        });

        return cursor;
    }

    private static Control BuildTableCell(string text, double baseFontSize, bool isHeader, TextAlignment alignment)
    {
        var block = new SelectableTextBlock
        {
            FontSize = baseFontSize,
            LineHeight = baseFontSize + 6,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isHeader
                ? Resolve("TextPrimaryBrush", Colors.White)
                : Resolve("TextSecondaryBrush", Colors.LightGray),
            TextAlignment = alignment,
        };
        AppendInlines(block.Inlines!, text, baseFontSize, baseFontSize);

        return new Border
        {
            Background = isHeader ? Resolve("Surface2Brush", Color.FromArgb(40, 128, 128, 128)) : null,
            BorderBrush = Resolve("BorderBrush", Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(10, 6),
            Child = block,
        };
    }

    // ── 行内解析 ────────────────────────────────────────────────────────────

    private enum SpanKind
    {
        Text,
        Bold,
        Italic,
        BoldItalic,
        Strike,
        Code,
        Link,
    }

    private readonly record struct Span(SpanKind Kind, string Text, string? Url = null);

    private static void AppendInlines(InlineCollection target, string text, double fontSize, double baseFontSize)
    {
        foreach (var span in ParseInline(text))
        {
            switch (span.Kind)
            {
                case SpanKind.Link:
                    target.Add(BuildLinkInline(span.Text, span.Url, fontSize));
                    break;

                case SpanKind.Code:
                    target.Add(BuildCodeInline(span.Text, fontSize, baseFontSize));
                    break;

                default:
                {
                    var run = new Run(span.Text) { FontSize = fontSize };
                    switch (span.Kind)
                    {
                        case SpanKind.Bold:
                            run.FontWeight = FontWeight.Bold;
                            break;
                        case SpanKind.Italic:
                            run.FontStyle = FontStyle.Italic;
                            break;
                        case SpanKind.BoldItalic:
                            run.FontWeight = FontWeight.Bold;
                            run.FontStyle = FontStyle.Italic;
                            break;
                        case SpanKind.Strike:
                            run.TextDecorations = TextDecorations.Strikethrough;
                            break;
                    }

                    target.Add(run);
                    break;
                }
            }
        }
    }

    private static InlineUIContainer BuildCodeInline(string text, double fontSize, double baseFontSize)
    {
        // 内联元素的高度必须小于行高，否则会把该行的基线顶下去，
        // 表现为含行内代码的行比相邻行低一截（看起来像压行）。
        return new InlineUIContainer
        {
            Child = new Border
            {
                Background = Resolve("Surface2Brush", Color.FromArgb(60, 128, 128, 128)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0),
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = MonoFont,
                    FontSize = Math.Max(10, fontSize - 1),
                    Foreground = Resolve("TextPrimaryBrush", Colors.White),
                },
            },
        };
    }

    private static InlineUIContainer BuildLinkInline(string text, string? url, double fontSize)
    {
        var link = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = Resolve("AccentBrush", Color.FromArgb(255, 100, 160, 255)),
            TextDecorations = TextDecorations.Underline,
            Cursor = CreateHandCursor(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            ToolTip.SetTip(link, url);
            link.PointerPressed += (_, args) =>
            {
                try
                {
                    Browser.Open(url);
                }
                catch
                {
                    // 打不开浏览器时保持文本可选中复制即可，不弹错误打断阅读
                }
                args.Handled = true;
            };
        }

        return new InlineUIContainer { Child = link };
    }

    private static List<Span> ParseInline(string text)
    {
        var spans = new List<Span>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new Span(SpanKind.Text, buffer.ToString()));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var fenceLength = 1;
                while (i + fenceLength < text.Length && text[i + fenceLength] == '`') fenceLength++;
                var fence = new string('`', fenceLength);
                var end = text.IndexOf(fence, i + fenceLength, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    spans.Add(new Span(SpanKind.Code, text[(i + fenceLength)..end].Trim()));
                    i = end + fenceLength;
                    continue;
                }
            }

            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryReadLink(text, i + 1, out var alt, out var imageUrl, out var afterImage))
                {
                    Flush();
                    spans.Add(new Span(SpanKind.Link, "🖼 " + (string.IsNullOrWhiteSpace(alt) ? imageUrl : alt), imageUrl));
                    i = afterImage;
                    continue;
                }
            }

            if (c == '[')
            {
                if (TryReadLink(text, i, out var label, out var url, out var afterLink))
                {
                    Flush();
                    spans.Add(new Span(SpanKind.Link, label, url));
                    i = afterLink;
                    continue;
                }
            }

            if (c == '*' || c == '_' || c == '~')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == c) run++;
                var marker = new string(c, Math.Min(run, 3));

                if (c == '~' && run < 2)
                {
                    buffer.Append(c);
                    i++;
                    continue;
                }

                if (marker is "**" or "__" or "***" or "___" or "~~")
                {
                    var end = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                    if (end > i + marker.Length)
                    {
                        Flush();
                        var kind = marker.Length == 2
                            ? (c == '~' ? SpanKind.Strike : SpanKind.Bold)
                            : SpanKind.BoldItalic;
                        spans.Add(new Span(kind, text[(i + marker.Length)..end]));
                        i = end + marker.Length;
                        continue;
                    }
                }
                else if (run == 1)
                {
                    var end = text.IndexOf(c.ToString(), i + 1, StringComparison.Ordinal);
                    if (end > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                    {
                        Flush();
                        spans.Add(new Span(SpanKind.Italic, text[(i + 1)..end]));
                        i = end + 1;
                        continue;
                    }
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    private static bool TryReadLink(string text, int start, out string label, out string url, out int next)
    {
        label = string.Empty;
        url = string.Empty;
        next = start;

        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        var paren = text.IndexOf(')', close + 2);
        if (paren < 0) return false;

        label = text[(start + 1)..close];
        url = text[(close + 2)..paren].Trim();
        next = paren + 1;
        return true;
    }

    // ── 识别辅助 ────────────────────────────────────────────────────────────

    private static bool IsBlockStart(string[] lines, int index)
    {
        var line = lines[index];
        return TryReadFence(line, out _)
               || IsDivider(line)
               || TryReadHeading(line, out _, out _)
               || line.TrimStart().StartsWith('>')
               || TryReadListMarker(line, out _, out _, out _, out _)
               || IsTableHeader(lines, index);
    }

    private static bool TryReadFence(string line, out string? language)
    {
        language = null;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return false;
        var info = trimmed[3..].Trim();
        language = string.IsNullOrWhiteSpace(info) ? null : info;
        return true;
    }

    private static bool IsDivider(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;
        return trimmed.All(c => c is '-' or '*' or '_') && trimmed.Distinct().Count() == 1;
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#')) return false;

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == '#') count++;
        if (count == 0 || count > 6 || count >= trimmed.Length || trimmed[count] != ' ') return false;

        level = count;
        text = trimmed[(count + 1)..].Trim().TrimEnd('#').Trim();
        return true;
    }

    private static bool TryReadListMarker(string line, out int indent, out bool ordered, out int number, out string text)
    {
        indent = 0;
        ordered = false;
        number = 1;
        text = string.Empty;

        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        indent = i;
        if (i >= line.Length) return false;

        var marker = line[i];
        if (marker is '-' or '*' or '+')
        {
            if (i + 1 >= line.Length || line[i + 1] != ' ') return false;
            text = line[(i + 2)..].Trim();
            return true;
        }

        if (!char.IsDigit(marker)) return false;

        var digitsStart = i;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i >= line.Length || line[i] is not ('.' or ')')) return false;
        if (i + 1 >= line.Length || line[i + 1] != ' ') return false;

        ordered = true;
        number = int.TryParse(line[digitsStart..i], out var parsed) ? parsed : 1;
        text = line[(i + 2)..].Trim();
        return true;
    }

    private static bool TryReadTaskState(string text, out bool isChecked, out string content)
    {
        isChecked = false;
        content = text;
        if (text.Length < 3 || text[0] != '[') return false;
        if (text[1] is not (' ' or 'x' or 'X') || text[2] != ']') return false;
        isChecked = text[1] is 'x' or 'X';
        content = text[3..].TrimStart();
        return true;
    }

    private static bool IsTableHeader(string[] lines, int index)
    {
        if (index + 1 >= lines.Length) return false;
        if (!lines[index].Contains('|') || !lines[index + 1].Contains('|')) return false;

        var separator = lines[index + 1].Trim();
        if (separator.Length == 0) return false;
        return separator.All(c => c is '|' or '-' or ':' or ' ' or '\t')
               && separator.Contains('-');
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static List<TextAlignment> ParseAlignments(string line, int count)
    {
        var cells = SplitTableRow(line);
        var result = new List<TextAlignment>(count);
        for (var i = 0; i < count; i++)
        {
            var cell = i < cells.Count ? cells[i] : string.Empty;
            var left = cell.StartsWith(':');
            var right = cell.EndsWith(':');
            result.Add(left && right ? TextAlignment.Center : right ? TextAlignment.Right : TextAlignment.Left);
        }
        return result;
    }

    private static string GetCell(List<string> cells, int index)
        => index < cells.Count ? cells[index] : string.Empty;

    /// <summary>手型光标依赖平台服务；无图形环境（如单测）时退化为默认光标。</summary>
    private static Cursor? CreateHandCursor()
    {
        try
        {
            return new Cursor(StandardCursorType.Hand);
        }
        catch
        {
            return null;
        }
    }

    private static IBrush Resolve(string key, Color fallback)
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }

    private sealed class ListEntry
    {
        public int Level { get; init; }
        public bool Ordered { get; init; }
        public int Number { get; init; }
        public string Text { get; set; } = string.Empty;
        public bool HasCheckbox { get; init; }
        public bool Checked { get; init; }
    }
}

/// <summary>把 Markdown 文本渲染为控件的内容容器，主题变化或文本更新时自动重建。</summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<double> BaseFontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(BaseFontSize), 13);

    public MarkdownView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        Rebuild();
        AttachedToVisualTree += (_, _) => Rebuild();
    }

    /// <summary>要渲染的 Markdown 原文。</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>正文字号，标题与代码块按比例推导。</summary>
    public double BaseFontSize
    {
        get => GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == BaseFontSizeProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild() => Content = MarkdownRenderer.Render(Markdown, BaseFontSize);
}
