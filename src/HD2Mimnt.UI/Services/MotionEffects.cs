using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace HD2Mimnt.UI.Services;

/// <summary>浮层入场动效。挂在会随 IsVisible 切换的容器上，显示时做一次淡入。</summary>
public static class MotionEffects
{
    private static readonly CubicEaseOut EaseOut = new();

    public static readonly AttachedProperty<bool> EntranceProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Entrance", typeof(MotionEffects));

    static MotionEffects()
    {
        EntranceProperty.Changed.AddClassHandler<Control>(OnEntranceChanged);
    }

    public static void SetEntrance(Control control, bool value) => control.SetValue(EntranceProperty, value);

    public static bool GetEntrance(Control control) => control.GetValue(EntranceProperty);

    private static void OnEntranceChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            control.PropertyChanged -= OnControlPropertyChanged;
            control.PropertyChanged += OnControlPropertyChanged;
            if (control.IsVisible) Schedule(control);
        }
        else
        {
            control.PropertyChanged -= OnControlPropertyChanged;
        }
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty || e.NewValue is not true || sender is not Control control) return;
        Schedule(control);
    }

    private static void Schedule(Control control)
    {
        if (!UiMotion.IsEnabled) return;
        // 等布局把新浮层排好再取基准位置，避免偏移动画从旧坐标开始。
        Dispatcher.UIThread.Post(() => PlayEntrance(control), DispatcherPriority.Loaded);
    }

    private static void PlayEntrance(Control control)
    {
        if (!UiMotion.IsEnabled || !control.IsVisible) return;

        var visual = ElementComposition.GetElementVisual(control);
        if (visual is null || visual.IsDisposed) return;

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = UiMotion.StandardDuration;
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, visual.Opacity, EaseOut);
        visual.StartAnimation("Opacity", opacity);
    }
}
