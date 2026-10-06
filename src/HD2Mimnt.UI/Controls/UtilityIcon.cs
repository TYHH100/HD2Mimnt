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
            ["Settings"] = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9A3 3 0 1 1 12 15A3 3 0 1 1 12 9",
            ["Sliders"] = "M3 7.5H10.3 M15.7 7.5H21 M13 5.3A2.2 2.2 0 1 1 13 9.7A2.2 2.2 0 1 1 13 5.3 M3 16.5H5.8 M10.2 16.5H21 M8 14.3A2.2 2.2 0 1 1 8 18.7A2.2 2.2 0 1 1 8 14.3",
            ["Plugin"] = "M8 3V7 M16 3V7 M5 7H19V11A7 7 0 0 1 5 11Z M12 18V22",
            ["Logs"] = "M14 2H4V22H20V8Z M14 2V8H20 M8 12H16 M8 16H16",
            ["Database"] = "M21 5A9 3 0 1 1 3 5A9 3 0 1 1 21 5 M3 5V19A9 3 0 0 0 21 19V5 M3 12A9 3 0 0 0 21 12",
            ["Code"] = "M8 6L2 12L8 18 M16 6L22 12L16 18 M14 4L10 20",
            ["Info"] = "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12 M12 11V17 M12 7H12.1",
            ["Refresh"] = "M20 7A9 9 0 0 0 4 7 M20 3V7H16 M4 17A9 9 0 0 0 20 17 M4 21V17H8",
            ["Deploy"] = "M12 16V3 M7 8L12 3L17 8 M3 15V21H21V15",
            ["Clean"] = "M3 6H21 M9 6V3H15V6 M5 6L6 21H18L19 6 M10 10V17 M14 10V17",
            ["Check"] = "M20 6L9 17L4 12",
            ["Copy"] = "M9 9H20V20H9Z M4 15V4H15",
            ["Edit"] = "M17 3L21 7L8 20H4V16L17 3",
            ["Home"] = "M3 10A2 2 0 0 1 3.71 8.47L10.71 2.47A2 2 0 0 1 13.29 2.47L20.29 8.47A2 2 0 0 1 21 10V19A2 2 0 0 1 19 21H14V14H10V21H5A2 2 0 0 1 3 19Z",
            ["ListOrdered"] = "M10 6H21 M10 12H21 M10 18H21 M4 5H6 M4 11H6 M4 17H6",
            ["Warning"] = "M21.73 18L13.73 4A2 2 0 0 0 10.25 4L2.25 18A2 2 0 0 0 4 21H20A2 2 0 0 0 21.73 18Z M12 9V14 M12 17H12.1",
            ["Wrench"] = "M14.7 6.3A1 1 0 0 0 14.7 7.7L16.3 9.3A1 1 0 0 0 17.7 9.3L21.47 5.54A6 6 0 0 1 13.53 13.47L6.62 20.38A2.12 2.12 0 0 1 3.62 17.38L10.53 10.47A6 6 0 0 1 18.47 2.54L14.7 6.3Z"
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
