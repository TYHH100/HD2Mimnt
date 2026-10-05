using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HD2Mimnt.UI.Controls;

// Lucide's 24px stroke geometry keeps tool icons consistent across every view.
public sealed class UtilityIcon : Control
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<UtilityIcon, string>(nameof(Kind), "Package");
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<UtilityIcon, IBrush?>(nameof(Foreground), Brushes.Gray);

    private static readonly IReadOnlyDictionary<string, Geometry> Shapes =
        new Dictionary<string, string>
        {
            ["Search"] = "M21 21L16.7 16.7 M19 11A8 8 0 1 1 3 11A8 8 0 1 1 19 11",
            ["Add"] = "M12 5V19 M5 12H19",
            ["Close"] = "M6 6L18 18 M18 6L6 18",
            ["More"] = "M5 12H5.1 M12 12H12.1 M19 12H19.1",
            ["Package"] = "M3 7L12 2L21 7V17L12 22L3 17Z M3 7L12 12L21 7 M12 12V22 M7.5 4.5L16.5 9.5",
            ["Bell"] = "M18 8A6 6 0 0 0 6 8C6 15 3 15 3 17H21C21 15 18 15 18 8 M10 21H14",
            ["Tasks"] = "M9 6H21 M9 12H21 M9 18H21 M3 6L4 7L6 5 M3 12L4 13L6 11 M3 18L4 19L6 17",
            ["Chevron"] = "M9 6L15 12L9 18",
            ["Back"] = "M19 12H5 M12 5L5 12L12 19",
            ["Folder"] = "M3 7V19H21V7H11L9 4H3Z",
            ["Settings"] = "M12 8A4 4 0 1 1 12 16A4 4 0 1 1 12 8 M12 2V5 M12 19V22 M2 12H5 M19 12H22 M5 5L7 7 M17 17L19 19 M5 19L7 17 M17 7L19 5",
            ["Plugin"] = "M8 3V7 M16 3V7 M5 7H19V11A7 7 0 0 1 5 11Z M12 18V22",
            ["Logs"] = "M14 2H4V22H20V8Z M14 2V8H20 M8 12H16 M8 16H16",
            ["Database"] = "M21 5A9 3 0 1 1 3 5A9 3 0 1 1 21 5 M3 5V19A9 3 0 0 0 21 19V5 M3 12A9 3 0 0 0 21 12",
            ["Code"] = "M8 6L2 12L8 18 M16 6L22 12L16 18 M14 4L10 20",
            ["Info"] = "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12 M12 11V17 M12 7H12.1",
            ["Refresh"] = "M20 7A9 9 0 0 0 4 7 M20 3V7H16 M4 17A9 9 0 0 0 20 17 M4 21V17H8",
            ["Deploy"] = "M12 16V3 M7 8L12 3L17 8 M3 15V21H21V15",
            ["Clean"] = "M3 6H21 M9 6V3H15V6 M5 6L6 21H18L19 6 M10 10V17 M14 10V17"
        }.ToDictionary(pair => pair.Key, pair => (Geometry)StreamGeometry.Parse(pair.Value));

    static UtilityIcon() => AffectsRender<UtilityIcon>(KindProperty, ForegroundProperty);

    public UtilityIcon() { Width = 16; Height = 16; }
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, new Pen(Foreground, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape);
    }
}
