using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaApplication = Avalonia.Application;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 暗潮外观的运行时落地：只改 AppResources 里已有的动态资源键和主题变体，
/// 颜色本身继续由 FluentAvalonia 主题提供，避免维护第二套深浅色画刷。
/// </summary>
public static class DarktideAppearance
{
    public static void ApplyTheme(AvaloniaApplication? app, string theme)
    {
        if (app == null) return;
        app.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    /// <summary>卡片透明度只改透明度，画刷颜色仍跟随主题。有背景图时顶栏跟随卡片透明度、
    /// 内容区全透明露出背景图；无背景图时全部恢复不透明。</summary>
    public static void Apply(AvaloniaApplication? app, string theme, double cardOpacityPercent, double cornerRadius,
        bool hasBackgroundImage)
    {
        if (app == null) return;
        ApplyTheme(app, theme);

        var percent = Math.Clamp(cardOpacityPercent, 20, 100);
        app.Resources["CardSurfaceOpacity"] = percent / 100.0;
        app.Resources["ChromeSurfaceOpacity"] = hasBackgroundImage ? percent / 100.0 : 1.0;
        app.Resources["ContentSurfaceOpacity"] = hasBackgroundImage ? 0.0 : 1.0;

        var radius = Math.Clamp(cornerRadius, 0, 16);
        app.Resources["CardCornerRadius"] = new CornerRadius(radius);
        app.Resources["OverlayCornerRadius"] = new CornerRadius(Math.Max(0, radius - 2));

    }
}
