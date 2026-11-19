using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HD2Mimnt.UI.Services;

/// <summary>把 @ 补全弹层对齐到搜索框的光标位置（左内边距 + 光标前文本宽度）。</summary>
internal static class TagPopupPlacement
{
    public static void AlignToCaret(TextBox box, Popup popup)
    {
        var text = box.Text ?? string.Empty;
        var caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        var prefix = text[..caret];
        var width = 0d;
        if (prefix.Length > 0)
        {
            var fontSize = double.IsNaN(box.FontSize) || box.FontSize <= 0 ? 14 : box.FontSize;
            var formatted = new FormattedText(prefix, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(box.FontFamily, box.FontStyle, box.FontWeight), fontSize, Brushes.Black);
            width = formatted.WidthIncludingTrailingWhitespace;
        }
        popup.HorizontalOffset = box.Padding.Left + width;
    }
}
