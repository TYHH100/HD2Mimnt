using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

/// <summary>
/// 声明式界面区块渲染器。
///
/// 插件只提供区块数据（文本、按钮、输入框、列表……），这里用管理器自己的控件
/// 生成界面，因此样式、主题、无障碍行为完全受控，插件也无法注入任意 XAML。
/// </summary>
public sealed class PluginUiPresenter : ContentControl
{
    public static readonly StyledProperty<PluginUiSurfaceViewModel?> SurfaceProperty =
        AvaloniaProperty.Register<PluginUiPresenter, PluginUiSurfaceViewModel?>(nameof(Surface));

    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _actionButtons = new();
    private PluginUiSurfaceViewModel? _surface;

    public PluginUiPresenter()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
    }

    public PluginUiSurfaceViewModel? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SurfaceProperty)
        {
            Attach(Surface);
        }
    }

    private void Attach(PluginUiSurfaceViewModel? surface)
    {
        if (_surface is { } previous)
        {
            previous.Blocks.CollectionChanged -= OnBlocksChanged;
            previous.PropertyChanged -= OnSurfacePropertyChanged;
        }

        _surface = surface;

        if (_surface is { } current)
        {
            current.Blocks.CollectionChanged += OnBlocksChanged;
            current.PropertyChanged += OnSurfacePropertyChanged;
        }

        Rebuild();
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginUiSurfaceViewModel.IsBusy)) return;
        var enabled = _surface?.IsBusy != true;
        foreach (var button in _actionButtons)
        {
            button.IsEnabled = enabled && button.Tag is not false;
        }
    }

    private void Rebuild()
    {
        _inputs.Clear();
        _actionButtons.Clear();

        var root = new StackPanel { Spacing = 12 };
        if (_surface is not { } surface)
        {
            Content = root;
            return;
        }

        foreach (var block in surface.Blocks)
        {
            var control = BuildBlock(surface, block);
            if (control is not null) root.Children.Add(control);
        }

        Content = root;
    }

    private Control? BuildBlock(PluginUiSurfaceViewModel surface, PluginUiBlock block)
    {
        switch ((block.Type ?? PluginUiBlockTypes.Text).Trim().ToLowerInvariant())
        {
            case PluginUiBlockTypes.Markdown:
                return new MarkdownView { Markdown = block.Text, BaseFontSize = 13 };

            case PluginUiBlockTypes.Text:
                return string.IsNullOrWhiteSpace(block.Text)
                    ? null
                    : new SelectableTextBlock
                    {
                        Text = block.Text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        LineHeight = 21,
                        Foreground = ThemeBrush("TextPrimaryBrush", Colors.White),
                    };

            case PluginUiBlockTypes.Heading:
            {
                var level = Math.Clamp(block.Level is <= 0 or > 4 ? 2 : block.Level, 1, 4);
                return new TextBlock
                {
                    Text = block.Text ?? block.Title,
                    FontSize = 22 - (level - 1) * 3,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextPrimaryBrush", Colors.White),
                    Margin = new Thickness(0, level == 1 ? 0 : 4, 0, 0),
                };
            }

            case PluginUiBlockTypes.Divider:
                return new Border
                {
                    Height = 1,
                    Background = ThemeBrush("BorderBrush", Color.FromArgb(60, 128, 128, 128)),
                };

            case PluginUiBlockTypes.Notice:
                return BuildNotice(block);

            case PluginUiBlockTypes.Facts:
                return BuildFacts(block);

            case PluginUiBlockTypes.List:
                return BuildList(block);

            case "input":
                return BuildInput(block);

            case PluginUiBlockTypes.Actions:
                return BuildActions(surface, block.Actions);

            default:
                // 未知类型按普通文本兜底，插件升级不会让页面整块消失
                return string.IsNullOrWhiteSpace(block.Text)
                    ? null
                    : new SelectableTextBlock { Text = block.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        }
    }

    private Control BuildNotice(PluginUiBlock block)
    {
        var (brushKey, backgroundKey, icon) = (block.Tone ?? PluginUiTones.Info).ToLowerInvariant() switch
        {
            PluginUiTones.Success => ("SuccessBrush", "SuccessBackgroundBrush", "Check"),
            PluginUiTones.Warning => ("WarningBrush", "WarningBackgroundBrush", "Warning"),
            PluginUiTones.Danger => ("ErrorBrush", "ErrorBackgroundBrush", "Warning"),
            _ => ("AccentBrush", "SelectedSurfaceBrush", "Info"),
        };

        // 用 Grid 而非横向 StackPanel：横向排布会以无限宽度测量子元素，
        // 长文本的 TextWrapping 失效并溢出右侧边界。
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var noticeIcon = new UtilityIcon
        {
            Kind = block.Icon ?? icon,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            Foreground = ThemeBrush(brushKey, Colors.SteelBlue),
        };
        Grid.SetColumn(noticeIcon, 0);
        panel.Children.Add(noticeIcon);

        var noticeText = new MarkdownView
        {
            Markdown = string.IsNullOrWhiteSpace(block.Title)
                ? block.Text
                : $"**{block.Title}**  {block.Text}",
            BaseFontSize = 12,
        };
        Grid.SetColumn(noticeText, 1);
        panel.Children.Add(noticeText);

        return new Border
        {
            Background = ThemeBrush(backgroundKey, Color.FromArgb(30, 128, 128, 128)),
            BorderBrush = ThemeBrush(brushKey, Colors.SteelBlue),
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = panel,
        };
    }

    private Control BuildFacts(PluginUiBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var row = 0;
        foreach (var fact in block.Facts)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = fact.Label,
                FontSize = 12,
                Margin = new Thickness(0, 2, 16, 2),
                Foreground = ThemeBrush("TextSecondaryBrush", Colors.LightGray),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var value = new SelectableTextBlock
            {
                Text = fact.Value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2),
                Foreground = ThemeBrush("TextPrimaryBrush", Colors.White),
            };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            row++;
        }

        return new Border
        {
            Background = ThemeBrush("Surface1Brush", Color.FromArgb(255, 28, 30, 36)),
            BorderBrush = ThemeBrush("BorderBrush", Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = grid,
        };
    }

    private Control BuildList(PluginUiBlock block)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var item in block.Items)
        {
            // 同 BuildNotice：横向 StackPanel 会让文本按无限宽度测量，
            // 长条目无法折行并溢出右边界，因此改用 Auto + * 两列。
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            Control marker;
            if (!string.IsNullOrWhiteSpace(item.Icon))
            {
                marker = new UtilityIcon
                {
                    Kind = item.Icon,
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 3, 8, 0),
                    Foreground = ThemeBrush("TextSecondaryBrush", Colors.LightGray),
                };
            }
            else
            {
                marker = new TextBlock
                {
                    Text = "•",
                    FontSize = 13,
                    // 与正文行高一致，避免符号比首行文字高出一截
                    LineHeight = 21,
                    MinWidth = 10,
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = ThemeBrush("TextSecondaryBrush", Colors.LightGray),
                };
            }

            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new SelectableTextBlock
            {
                Text = item.Text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextPrimaryBrush", Colors.White),
            });
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                text.Children.Add(new SelectableTextBlock
                {
                    Text = item.Detail,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextMutedBrush", Colors.Gray),
                });
            }

            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control BuildInput(PluginUiBlock block)
    {
        var input = block.Input ?? new PluginUiInput { Id = block.Id ?? "value" };
        var id = string.IsNullOrWhiteSpace(input.Id) ? block.Id ?? "value" : input.Id;

        var textBox = new TextBox
        {
            Text = input.Value ?? string.Empty,
            PlaceholderText = input.Placeholder,
            AcceptsReturn = input.Multiline,
            TextWrapping = input.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = input.Multiline ? Math.Clamp(input.Rows, 2, 12) * 22 : 0,
            VerticalContentAlignment = input.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };

        _inputs[id] = textBox;

        var panel = new StackPanel { Spacing = 6 };
        var label = block.Title ?? input.Label;
        if (!string.IsNullOrWhiteSpace(label))
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = ThemeBrush("TextSecondaryBrush", Colors.LightGray),
            });
        }
        panel.Children.Add(textBox);
        return panel;
    }

    private Control BuildActions(PluginUiSurfaceViewModel surface, List<PluginUiAction> actions)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var action in actions)
        {
            var granted = surface.HasPermission(action.RequiredPermission);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(action.Icon))
            {
                content.Children.Add(new UtilityIcon { Kind = action.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
            }
            content.Children.Add(new TextBlock
            {
                Text = granted ? action.Label : $"{action.Label}（需要授权）",
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 6) };
            var style = (action.Style ?? PluginUiActionStyles.Secondary).ToLowerInvariant();
            if (style == PluginUiActionStyles.Primary)
            {
                button.Classes.Add("accent");
            }
            else if (style == PluginUiActionStyles.Danger)
            {
                button.Foreground = ThemeBrush("ErrorBrush", Colors.IndianRed);
            }

            if (!granted)
            {
                button.Tag = false;
                ToolTip.SetTip(button, $"需要权限“{action.RequiredPermission}”，请在设置 → 插件中授权。");
            }

            button.IsEnabled = granted && surface.IsBusy == false;
            button.Click += async (_, _) =>
            {
                if (surface.IsBusy) return;
                var values = CollectInputValues();
                await surface.ExecuteAsync(action, values);
            };

            _actionButtons.Add(button);
            panel.Children.Add(button);
        }

        return panel;
    }

    private Dictionary<string, string> CollectInputValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, box) in _inputs)
        {
            values[id] = box.Text ?? string.Empty;
        }
        return values;
    }

    private static IBrush ThemeBrush(string key, Color fallback)
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }
}
