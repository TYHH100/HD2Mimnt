using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI.Services;
using System.Globalization;

namespace HD2Mimnt.UITests;

public class ThemeSkinUiTests
{
    [AvaloniaFact]
    public void OreTheme_ShouldOverrideSemanticResources_AndRestoreOnLeave()
    {
        var app = global::Avalonia.Application.Current!;
        var originalVariant = app.RequestedThemeVariant;
        var originalTheme = originalVariant == ThemeVariant.Light ? AppThemeService.Light
            : originalVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        try
        {
            var baseMergedCount = app.Resources.MergedDictionaries.Count;
            var baseStyleCount = app.Styles.Count;

            AppThemeService.Apply(AppThemeService.Ore);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#FF1E1E1E"), GetBrush(app, "Surface0Brush").Color);
            Assert.Equal(new CornerRadius(0), GetCornerRadius(app, "CardCornerRadius"));
            Assert.Equal(baseMergedCount + 2, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount + 1, app.Styles.Count);

            AppThemeService.Apply(AppThemeService.Ore);
            Assert.Equal(baseMergedCount + 2, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount + 1, app.Styles.Count);

            AppThemeService.Apply(AppThemeService.Dark);
            Assert.Equal(baseMergedCount, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount, app.Styles.Count);
            Assert.NotEqual(Color.Parse("#FF1E1E1E"), GetBrush(app, "Surface0Brush").Color);
        }
        finally
        {
            app.Resources["CardSurfaceOpacity"] = 1.0;
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void OreTheme_ShouldRestyleExistingControls()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        var button = new Button { Content = "test" };
        var window = new Window { Content = button, Width = 240, Height = 120 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<ContentPresenter>(button.GetVisualChildren().Single());

            AppThemeService.Apply(AppThemeService.Ore);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<Border>(button.GetVisualChildren().Single());

            AppThemeService.Apply(AppThemeService.Dark);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<ContentPresenter>(button.GetVisualChildren().Single());
        }
        finally
        {
            window.Close();
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void OreTheme_ShouldSwapToggleTemplateAtRuntime()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        var toggle = new ToggleSwitch { IsChecked = true };
        var window = new Window { Content = toggle, Width = 240, Height = 120 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(toggle.GetVisualDescendants().Any(Named("FocusRing")));

            AppThemeService.Apply(AppThemeService.Ore);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(toggle.GetVisualDescendants().Any(Named("FocusRing")));
            Assert.True(toggle.GetVisualDescendants().Any(Named("PART_MovingKnobs")));

            AppThemeService.Apply(AppThemeService.Dark);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(toggle.GetVisualDescendants().Any(Named("FocusRing")));
        }
        finally
        {
            window.Close();
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void HelldiversTheme_ShouldOverrideSemanticResources_AndRestoreOnLeave()
    {
        var app = global::Avalonia.Application.Current!;
        var originalVariant = app.RequestedThemeVariant;
        var originalTheme = originalVariant == ThemeVariant.Light ? AppThemeService.Light
            : originalVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        try
        {
            var baseMergedCount = app.Resources.MergedDictionaries.Count;
            var baseStyleCount = app.Styles.Count;

            AppThemeService.Apply(AppThemeService.Helldivers);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#FF121212"), GetBrush(app, "Surface0Brush").Color);
            Assert.Equal(new CornerRadius(0), GetCornerRadius(app, "CardCornerRadius"));
            Assert.Equal(baseMergedCount + 2, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount + 1, app.Styles.Count);

            AppThemeService.Apply(AppThemeService.Helldivers);
            Assert.Equal(baseMergedCount + 2, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount + 1, app.Styles.Count);

            AppThemeService.Apply(AppThemeService.Dark);
            Assert.Equal(baseMergedCount, app.Resources.MergedDictionaries.Count);
            Assert.Equal(baseStyleCount, app.Styles.Count);
            Assert.NotEqual(Color.Parse("#FF121212"), GetBrush(app, "Surface0Brush").Color);
        }
        finally
        {
            app.Resources["CardSurfaceOpacity"] = 1.0;
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void HelldiversTheme_ShouldRestyleExistingControls()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        var button = new Button { Content = "test" };
        var window = new Window { Content = button, Width = 240, Height = 120 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<ContentPresenter>(button.GetVisualChildren().Single());

            AppThemeService.Apply(AppThemeService.Helldivers);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<Border>(button.GetVisualChildren().Single());

            AppThemeService.Apply(AppThemeService.Dark);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsType<ContentPresenter>(button.GetVisualChildren().Single());
        }
        finally
        {
            window.Close();
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void OreTheme_ShouldResolveDisplayFontFromLooseFile_AndRestoreOnLeave()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        try
        {
            // 显示字体以独立文件随包发布：不再内嵌后仍要能从应用目录取到 Monocraft 字形。
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Monocraft.ttf")));

            AppThemeService.Apply(AppThemeService.Ore);
            var font = GetDisplayFont(app);
            Assert.DoesNotContain("avares", font.ToString());
            Assert.Contains($"{FontManager.FontCollectionScheme}:", font.ToString());

            // Monocraft 是等宽像素字体；一旦回退到系统比例字体，同字数的不同字符宽度就会不一致。
            Assert.Equal(WidthOf(font, "0000"), WidthOf(font, "IIII"));

            AppThemeService.Apply(AppThemeService.Dark);
            Assert.DoesNotContain("Monocraft", GetDisplayFont(app).ToString());
        }
        finally
        {
            AppThemeService.Apply(originalTheme);
        }
    }

    [AvaloniaFact]
    public void HelldiversTheme_ShouldNotInheritOreDisplayFont()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        try
        {
            AppThemeService.Apply(AppThemeService.Helldivers);
            Assert.DoesNotContain("Monocraft", GetDisplayFont(app).ToString());
        }
        finally
        {
            AppThemeService.Apply(originalTheme);
        }
    }

    private static Func<Avalonia.Visual, bool> Named(string name) =>
        visual => visual is Avalonia.INamed named && named.Name == name;

    [AvaloniaFact]
    public void OreTheme_ShouldKeepRuntimeCardOpacityBinding()
    {
        var app = global::Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant == ThemeVariant.Light ? AppThemeService.Light
            : app.RequestedThemeVariant == ThemeVariant.Default ? AppThemeService.System
            : AppThemeService.Dark;
        try
        {
            AppThemeService.Apply(AppThemeService.Ore);
            app.Resources["CardSurfaceOpacity"] = 0.4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0.4, GetBrush(app, "CardFillBrush").Opacity, 3);
        }
        finally
        {
            app.Resources["CardSurfaceOpacity"] = 1.0;
            AppThemeService.Apply(originalTheme);
        }
    }

    private static ISolidColorBrush GetBrush(global::Avalonia.Application app, string key)
    {
        Assert.True(app.Resources.TryGetResource(key, app.ActualThemeVariant, out var value), $"缺少资源 {key}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    private static CornerRadius GetCornerRadius(global::Avalonia.Application app, string key)
    {
        Assert.True(app.Resources.TryGetResource(key, app.ActualThemeVariant, out var value), $"缺少资源 {key}");
        return Assert.IsType<CornerRadius>(value);
    }

    private static FontFamily GetDisplayFont(global::Avalonia.Application app)
    {
        Assert.True(app.Resources.TryGetResource(DisplayFontService.DisplayFontKey, app.ActualThemeVariant, out var value),
            "缺少显示字体资源");
        return Assert.IsType<FontFamily>(value);
    }

    private static double WidthOf(FontFamily font, string text) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal), 20, Brushes.Black).Width;
}
