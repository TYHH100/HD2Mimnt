using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.UI.Controls;

/// <summary>IDM 风格的分段下载进度条：按 HTTP Range 分段绘制各自的填充进度。</summary>
public class SegmentedDownloadBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<DownloadSegmentProgress>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedDownloadBar, IReadOnlyList<DownloadSegmentProgress>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SegmentedDownloadBar, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedDownloadBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SegmentedDownloadBar, double>(nameof(Gap), 3);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<SegmentedDownloadBar, double>(nameof(CornerRadius), 2);

    public IReadOnlyList<DownloadSegmentProgress>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static SegmentedDownloadBar()
    {
        AffectsRender<SegmentedDownloadBar>(SegmentsProperty, FillBrushProperty, TrackBrushProperty, GapProperty, CornerRadiusProperty);
        AffectsMeasure<SegmentedDownloadBar>(SegmentsProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(availableSize.Width, double.IsInfinity(availableSize.Height) ? 8 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        if (segments is not { Count: > 0 }) return;
        var total = segments[^1].End + 1;
        if (total <= 0) return;

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var gap = Gap;
        var radius = (float)Math.Min(CornerRadius, bounds.Height / 2);
        var usableWidth = Math.Max(bounds.Width - gap * (segments.Count - 1), 0);

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var width = (segment.End - segment.Start + 1) * usableWidth / total;
            var x = bounds.X + index * gap + segment.Start * usableWidth / total;
            var rect = new Rect(x, bounds.Y, width, bounds.Height);

            if (TrackBrush is { } track)
                context.FillRectangle(track, rect, radius);
            var downloaded = Math.Clamp(segment.Downloaded, 0, segment.End - segment.Start + 1);
            if (FillBrush is { } fill && downloaded > 0)
                context.FillRectangle(fill, new Rect(rect.X, rect.Y, width * downloaded / (segment.End - segment.Start + 1), rect.Height), radius);
        }
    }
}
