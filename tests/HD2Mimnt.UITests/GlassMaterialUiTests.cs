using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Data.Sqlite;
using Moq;

namespace HD2Mimnt.UITests;

/// <summary>浅色主题毛玻璃：平台没有模糊时不能把窗口做成透出桌面的半透明，强度也只能增加通透度。</summary>
public sealed class GlassMaterialUiTests
{
    [Fact]
    public void OpacityCurve_ShouldOnlyAddTranslucency()
    {
        Assert.Equal(1d, GlassMaterialService.WindowOpacity(GlassMaterialService.MinIntensity));
        Assert.Equal(0.62d, GlassMaterialService.WindowOpacity(GlassMaterialService.MaxIntensity));
        Assert.True(GlassMaterialService.WindowOpacity(80) < GlassMaterialService.WindowOpacity(40));

        // 卡片始终比背景更实：强度只把卡片下调到下限，不会抬高用户设置的更低值。
        Assert.Equal(1d, GlassMaterialService.CardOpacity(0, 100));
        Assert.Equal(0.9d, GlassMaterialService.CardOpacity(100, 100));
        Assert.Equal(0.4d, GlassMaterialService.CardOpacity(100, 40));
        Assert.True(GlassMaterialService.CardOpacity(100, 100) > GlassMaterialService.WindowOpacity(100));

        // 仅浅色主题、且没有自定义背景图（背景图自己就是底层内容）时才请求毛玻璃。
        Assert.True(GlassMaterialService.IsRequested(ThemeVariant.Light, 10, false));
        Assert.False(GlassMaterialService.IsRequested(ThemeVariant.Light, 0, false));
        Assert.False(GlassMaterialService.IsRequested(ThemeVariant.Dark, 100, false));
        Assert.False(GlassMaterialService.IsRequested(ThemeVariant.Light, 100, true));
    }

    [AvaloniaFact]
    public void WithoutPlatformBlur_ShouldKeepOpaqueSurfaces()
    {
        var app = Avalonia.Application.Current!;
        var window = new Window { Width = 420, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 无头平台不提供背景模糊：即使浅色主题 + 最高强度也必须退回不透明，避免露出未模糊的桌面。
            var active = GlassMaterialService.Apply(
                window, ThemeVariant.Light, GlassMaterialService.MaxIntensity, LiveBackdropService.DefaultBlur,
                hasBackgroundImage: false, cardOpacityPercent: 100);

            Assert.False(active);
            Assert.Equal(0d, Opacity(app, "ShellSurfaceOpacity"));
            Assert.Equal(1d, Opacity(app, "ContentSurfaceOpacity"));
            Assert.Equal(1d, Opacity(app, "ChromeSurfaceOpacity"));
            Assert.Equal(1d, Opacity(app, "CardSurfaceOpacity"));

            // 原有行为保持不变：有背景图时内容区全透明、顶栏与卡片跟随卡片不透明度。
            GlassMaterialService.Apply(
                window, ThemeVariant.Light, GlassMaterialService.MaxIntensity, LiveBackdropService.DefaultBlur,
                hasBackgroundImage: true, cardOpacityPercent: 60);
            Assert.Equal(0d, Opacity(app, "ContentSurfaceOpacity"));
            Assert.Equal(0.6d, Opacity(app, "ChromeSurfaceOpacity"));
            Assert.Equal(0.6d, Opacity(app, "CardSurfaceOpacity"));
            Assert.Equal(0d, Opacity(app, "ShellSurfaceOpacity"));
        }
        finally
        {
            window.Close();
            foreach (var key in new[] { "ShellSurfaceOpacity", "ContentSurfaceOpacity", "ChromeSurfaceOpacity", "CardSurfaceOpacity" })
                app.Resources[key] = 1d;
            app.Resources["ShellSurfaceOpacity"] = 0d;
        }
    }

    [AvaloniaFact]
    public void AppearanceSettings_ShouldExposeGlassIntensitySlider()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-glass-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Window? window = null;
        SettingsViewModel? viewModel = null;
        try
        {
            var settings = new SettingsService(root);
            var imageCache = new Mock<IImageCacheService>();
            viewModel = new SettingsViewModel(settings, new GameDetectionService(settings), new NotificationService(), imageCache.Object)
            {
                SelectedCategory = "外观",
                // 毛玻璃强度只在浅色主题下展示。
                Theme = AppThemeService.Light,
                GlassIntensity = 35,
                GlassBlur = 24
            };
            window = new Window { Width = 1000, Height = 760, Content = new SettingsView { DataContext = viewModel } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var slider = window.GetVisualDescendants().OfType<Slider>()
                .FirstOrDefault(control => control.Name == "GlassIntensitySlider");
            Assert.NotNull(slider);
            Assert.Equal(0d, slider!.Minimum);
            Assert.Equal(100d, slider.Maximum);
            Assert.Equal(35d, slider.Value);

            viewModel.GlassIntensity = 70;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(70d, slider.Value);

            // 模糊强度：0 为纯玻璃，48 为最朦胧，滑块上下限与换算一致。
            var blurSlider = window.GetVisualDescendants().OfType<Slider>()
                .FirstOrDefault(control => control.Name == "GlassBlurSlider");
            Assert.NotNull(blurSlider);
            Assert.Equal(LiveBackdropService.MinBlur, blurSlider!.Minimum);
            Assert.Equal(LiveBackdropService.MaxBlur, blurSlider.Maximum);
            Assert.Equal(24d, blurSlider.Value);

            // 可调模糊玻璃默认关闭：走系统玻璃（窗口能被正常截图），模糊半径由系统固定，滑块置灰。
            var liveToggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .FirstOrDefault(control => control.Name == "GlassLiveBackdropToggle");
            Assert.NotNull(liveToggle);
            Assert.False(liveToggle!.IsChecked);
            Assert.False(viewModel.IsGlassBlurEnabled);
            Assert.False(blurSlider.IsEffectivelyEnabled);

            // 开启后改用自绘玻璃，模糊滑块才有作用对象。
            viewModel.GlassLiveBackdrop = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(liveToggle.IsChecked);
            Assert.True(viewModel.IsGlassBlurEnabled);
            Assert.True(blurSlider.IsEffectivelyEnabled);

            // 毛玻璃关闭（强度 0）时同样没有作用对象，模糊滑块置灰。
            viewModel.GlassIntensity = 0;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(viewModel.IsGlassBlurEnabled);
            Assert.False(blurSlider.IsEffectivelyEnabled);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BlurRange_ShouldCoverPureGlassToFullFrost()
    {
        Assert.Equal(0d, LiveBackdropService.MinBlur);
        Assert.True(LiveBackdropService.DefaultBlur > LiveBackdropService.MinBlur);
        Assert.True(LiveBackdropService.MaxBlur > LiveBackdropService.DefaultBlur);

        // 取屏必须比窗口多取一圈，否则模糊会在窗口边缘取到空像素。
        Assert.True(LiveBackdropService.BlurMargin >= LiveBackdropService.MaxBlur);
    }

    [AvaloniaFact]
    public void GlassIntensitySlider_ShouldOnlyShowForLightTheme()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-glass-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SettingsViewModel? viewModel = null;
        try
        {
            var settings = new SettingsService(root);
            var imageCache = new Mock<IImageCacheService>();
            viewModel = new SettingsViewModel(settings, new GameDetectionService(settings), new NotificationService(), imageCache.Object)
            {
                SelectedCategory = "外观"
            };

            // 深色 / 皮肤主题不参与毛玻璃，该项不展示。
            foreach (var theme in new[] { AppThemeService.Dark, AppThemeService.Ore, AppThemeService.Helldivers })
            {
                viewModel.Theme = theme;
                Assert.False(viewModel.IsGlassMaterialVisible);
            }

            viewModel.Theme = AppThemeService.Light;
            Assert.True(viewModel.IsGlassMaterialVisible);

            // 默认走系统玻璃（窗口能被截图），模糊半径不可调；开启自绘玻璃后才可调。
            Assert.False(viewModel.IsGlassBlurEnabled);
            viewModel.GlassLiveBackdrop = true;
            Assert.True(viewModel.IsGlassBlurEnabled);
        }
        finally
        {
            viewModel?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static double Opacity(Avalonia.Application app, string key) => Convert.ToDouble(app.Resources[key]);
}
