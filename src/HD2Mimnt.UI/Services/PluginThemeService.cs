using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 插件主题包应用器。
///
/// 只接受白名单内的设计令牌（颜色 / 圆角 / 字号），且值必须是字面量，
/// 插件无法提供样式代码、资源键或任意 XAML——既能大幅换肤，又不会破坏
/// 控件语义或读取到界面内部状态。应用是增量合并，还原时整体移除。
/// </summary>
public static class PluginThemeService
{
    private enum TokenKind
    {
        Brush,
        CornerRadius,
        Double,
    }

    private sealed record TokenDefinition(TokenKind Kind, string ResourceKey, double Min, double Max);

    private static readonly Dictionary<string, TokenDefinition> Tokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accent"] = new(TokenKind.Brush, "AccentBrush", 0, 0),
            ["accentStrong"] = new(TokenKind.Brush, "AccentStrongBrush", 0, 0),
            ["surface0"] = new(TokenKind.Brush, "Surface0Brush", 0, 0),
            ["surface1"] = new(TokenKind.Brush, "Surface1Brush", 0, 0),
            ["surface2"] = new(TokenKind.Brush, "Surface2Brush", 0, 0),
            ["surface3"] = new(TokenKind.Brush, "Surface3Brush", 0, 0),
            ["border"] = new(TokenKind.Brush, "BorderBrush", 0, 0),
            ["borderStrong"] = new(TokenKind.Brush, "BorderStrongBrush", 0, 0),
            ["textPrimary"] = new(TokenKind.Brush, "TextPrimaryBrush", 0, 0),
            ["textSecondary"] = new(TokenKind.Brush, "TextSecondaryBrush", 0, 0),
            ["textMuted"] = new(TokenKind.Brush, "TextMutedBrush", 0, 0),
            ["success"] = new(TokenKind.Brush, "SuccessBrush", 0, 0),
            ["warning"] = new(TokenKind.Brush, "WarningBrush", 0, 0),
            ["error"] = new(TokenKind.Brush, "ErrorBrush", 0, 0),
            ["hoverSurface"] = new(TokenKind.Brush, "HoverSurfaceBrush", 0, 0),
            ["selectedSurface"] = new(TokenKind.Brush, "SelectedSurfaceBrush", 0, 0),
            ["cardCornerRadius"] = new(TokenKind.CornerRadius, "CardCornerRadius", 0, 32),
            ["overlayCornerRadius"] = new(TokenKind.CornerRadius, "OverlayCornerRadius", 0, 32),
            ["notificationCornerRadius"] = new(TokenKind.CornerRadius, "NotificationCornerRadius", 0, 40),
            ["subtleCornerRadius"] = new(TokenKind.CornerRadius, "SubtleCornerRadius", 0, 24),
            ["pageTitleFontSize"] = new(TokenKind.Double, "PageTitleFontSize", 14, 48),
            ["sectionTitleFontSize"] = new(TokenKind.Double, "SectionTitleFontSize", 12, 36),
            ["bodyFontSize"] = new(TokenKind.Double, "BodyFontSize", 10, 24),
            ["captionFontSize"] = new(TokenKind.Double, "CaptionFontSize", 8, 20),
        };

    private static ResourceDictionary? _activeDictionary;

    /// <summary>当前生效的插件主题；null 表示默认主题。</summary>
    public static PluginThemeContribution? ActiveTheme { get; private set; }

    public static string? ActivePluginName { get; private set; }

    public static event EventHandler? Changed;

    /// <summary>白名单令牌名，供文档与错误提示使用。</summary>
    public static IReadOnlyCollection<string> SupportedTokens => Tokens.Keys;

    /// <summary>
    /// 应用主题包。返回被忽略的令牌（未知键或值非法），便于界面提示作者。
    /// </summary>
    public static IReadOnlyList<string> Apply(PluginUiBundle bundle, PluginThemeContribution theme)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null)
        {
            return Array.Empty<string>();
        }

        Reset();

        var dictionary = new ResourceDictionary();
        var ignored = new List<string>();

        foreach (var (key, rawValue) in theme.Tokens)
        {
            if (!Tokens.TryGetValue(key, out var definition) || string.IsNullOrWhiteSpace(rawValue))
            {
                ignored.Add(key);
                continue;
            }

            if (TryCreateValue(definition, rawValue, out var value))
            {
                dictionary[definition.ResourceKey] = value;
            }
            else
            {
                ignored.Add(key);
            }
        }

        if (dictionary.Count == 0)
        {
            return ignored;
        }

        app.Resources.MergedDictionaries.Add(dictionary);
        _activeDictionary = dictionary;
        ActiveTheme = theme;
        ActivePluginName = bundle.PluginName;
        Changed?.Invoke(null, EventArgs.Empty);

        return ignored;
    }

    /// <summary>移除插件主题，恢复管理器自带配色。</summary>
    public static void Reset()
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null && _activeDictionary is not null)
        {
            app.Resources.MergedDictionaries.Remove(_activeDictionary);
        }

        _activeDictionary = null;
        ActiveTheme = null;
        ActivePluginName = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool TryCreateValue(TokenDefinition definition, string rawValue, out object? value)
    {
        value = null;
        var text = rawValue.Trim();

        switch (definition.Kind)
        {
            case TokenKind.Brush:
                if (!TryParseColor(text, out var color)) return false;
                value = new SolidColorBrush(color);
                return true;

            case TokenKind.CornerRadius:
            {
                var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is not (1 or 4)) return false;

                var numbers = new double[4];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!TryParseNumber(parts[i], definition, out numbers[i])) return false;
                }

                if (parts.Length == 1)
                {
                    numbers[1] = numbers[2] = numbers[3] = numbers[0];
                }

                value = new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[3]);
                return true;
            }

            case TokenKind.Double:
                if (!TryParseNumber(text, definition, out var number)) return false;
                value = number;
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseNumber(string text, TokenDefinition definition, out double number)
    {
        number = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        return number >= definition.Min && number <= definition.Max;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        if (text.Length is not (4 or 7 or 9) || text[0] != '#') return false;
        return Color.TryParse(text, out color);
    }
}
