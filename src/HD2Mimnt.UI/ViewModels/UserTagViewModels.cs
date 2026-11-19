using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>用户标签配色：未单独配置时按标签名稳定取色，保证同一标签处处同色。</summary>
public static class UserTagPalette
{
    public static readonly IReadOnlyList<string> Colors =
    [
        "#4F8DFD", "#3FB950", "#D29922", "#F85149", "#A371F7",
        "#DB61A2", "#39C5CF", "#D18616", "#8B949E", "#2EA8E5"
    ];

    public static string ColorFor(string tag, IReadOnlyDictionary<string, string>? custom)
    {
        if (custom != null && custom.TryGetValue(tag, out var color) && Color.TryParse(color, out _))
            return color;
        var hash = 0;
        foreach (var ch in tag) hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return Colors[(hash & 0x7FFFFFFF) % Colors.Count];
    }

    public static IBrush BrushFor(string tag, IReadOnlyDictionary<string, string>? custom)
        => new SolidColorBrush(Color.Parse(ColorFor(tag, custom)));
}

/// <summary>卡片/列表里的只读标签胶囊。</summary>
public sealed record UserTagChipViewModel(string Name, IBrush Brush);

/// <summary>详情面板里可改色的用户标签；颜色变化交给宿主持久化。</summary>
public sealed partial class UserTagItemViewModel : ObservableObject
{
    private readonly Func<string, Color, Task>? _colorChanged;
    private bool _suppressColorCallback;

    public string Name { get; }

    [ObservableProperty]
    private Color _color;

    public IBrush Brush => new SolidColorBrush(Color);

    public UserTagItemViewModel(string name, string hex, Func<string, Color, Task>? colorChanged)
    {
        Name = name;
        _colorChanged = colorChanged;
        _color = Color.TryParse(hex, out var color) ? color : Colors.ForestGreen;
    }

    /// <summary>宿主回写颜色时静默更新，避免再次触发保存或重建取色面板。</summary>
    public void ApplyColorSilently(Color color)
    {
        _suppressColorCallback = true;
        try { Color = color; }
        finally { _suppressColorCallback = false; }
    }

    partial void OnColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Brush));
        if (!_suppressColorCallback && _colorChanged != null) _ = _colorChanged(Name, value);
    }
}
