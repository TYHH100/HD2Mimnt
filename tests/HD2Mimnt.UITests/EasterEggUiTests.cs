using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task VersionBadgeSecretClicks_ShouldToggleWholePageJellyIndefinitely()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            main.NotificationPanelViewModel.DismissAllToasts();
            Border? card = null;
            for (var attempt = 0; attempt < 100 && card == null; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                card = window.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("mod-card") && border.IsEffectivelyVisible);
                if (card == null) await Task.Delay(10);
            }
            Assert.NotNull(card);
            await Task.Delay(1500);
            Dispatcher.UIThread.RunJobs();
            main.NotificationPanelViewModel.DismissAllToasts();
            window.UpdateLayout();

            var badge = window.FindControl<Border>("VersionBadge")!;
            var surface = window.FindControl<Grid>("ApplicationSurface")!;
            var point = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), window)!.Value;
            var jelly = GetJellyAnimator(window);
            var cardVisual = ElementComposition.GetElementVisual(card)!;
            var originalScale = cardVisual.Scale;
            var originalCenterPoint = cardVisual.CenterPoint;
            var originalTransform = card.RenderTransform;
            var region = CaptureRegion(window, card);
            var baseline = ReadPixels(window, region);

            for (var i = 0; i < 6; i++) Click(window, point);
            Assert.False(jelly.IsActive);

            Click(window, point);
            Assert.True(jelly.IsActive);
            Assert.True(jelly.ActiveTargetCount > 40);
            await Task.Delay(1200);
            var active = ReadPixels(window, region);
            var activeDiff = CountDifferent(baseline, active);
            Assert.True(activeDiff > 1000, $"active pixels differ: {activeDiff}");
            Assert.Same(originalTransform, card.RenderTransform);
            Assert.Null(surface.RenderTransform);

            await Task.Delay(TimeSpan.FromSeconds(3.2));
            Assert.True(jelly.IsActive);
            Assert.True(jelly.ActiveTargetCount > 40);

            for (var i = 0; i < 7; i++) Click(window, point);
            Assert.False(jelly.IsActive);
            Assert.Equal(0, jelly.ActiveTargetCount);
            await Task.Delay(200);
            await cardVisual.Compositor.RequestCommitAsync();
            var restored = ReadPixels(window, region);
            var restoredDiff = CountDifferent(baseline, restored);
            await Task.Delay(300);
            var restoredAgain = ReadPixels(window, region);
            var drift = CountDifferent(restored, restoredAgain);
            Assert.True(restoredDiff < 200,
                $"restored pixels differ: {restoredDiff}; active: {activeDiff}; drift: {drift}");
            Assert.True(drift < 50, $"restored frames drift: {drift}");
            Assert.Equal(originalScale, cardVisual.Scale);
            Assert.Equal(originalCenterPoint, cardVisual.CenterPoint);
            Assert.Same(originalTransform, card.RenderTransform);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }

    private static PageJellyAnimator GetJellyAnimator(MainWindow window) =>
        (PageJellyAnimator)typeof(MainWindow)
            .GetField("_pageJelly", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static PixelRect CaptureRegion(Window window, Control card)
    {
        var origin = card.TranslatePoint(default, window) ?? default;
        var x = Math.Max(0, (int)origin.X - 24);
        var y = Math.Max(0, (int)origin.Y - 24);
        var right = Math.Min((int)window.Bounds.Width, (int)(origin.X + card.Bounds.Width) + 24);
        var bottom = Math.Min((int)window.Bounds.Height, (int)(origin.Y + card.Bounds.Height) + 24);
        return new PixelRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static byte[] ReadPixels(Window window, PixelRect rect)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(60);
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var bytesPerPixel = 4;
        var rowBytes = rect.Width * bytesPerPixel;
        var data = new byte[rowBytes * rect.Height];
        for (var row = 0; row < rect.Height; row++)
        {
            Marshal.Copy(
                IntPtr.Add(buffer.Address, (rect.Y + row) * buffer.RowBytes + rect.X * bytesPerPixel),
                data, row * rowBytes, rowBytes);
        }
        return data;
    }

    private static int CountDifferent(byte[] left, byte[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        var different = Math.Abs(left.Length - right.Length);
        for (var i = 0; i < count; i++)
            if (left[i] != right[i]) different++;
        return different;
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
