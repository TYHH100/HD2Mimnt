using System.Windows.Input;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Controls;

/// <summary>
/// 右键菜单条目定义：之后任何页面需要右键菜单时，构造条目列表交给 AppContextMenu.Show 即可。
/// </summary>
public sealed class ContextMenuItem
{
    public ContextMenuItem(string header, ICommand command, string? iconKind = null, bool isDanger = false)
    {
        Header = header;
        Command = command;
        IconKind = iconKind;
        IsDanger = isDanger;
    }

    /// <summary>菜单文字。</summary>
    public string Header { get; }

    /// <summary>点击执行的命令（通常是 ViewModel 上的 RelayCommand 生成属性）。</summary>
    public ICommand Command { get; }

    /// <summary>可选命令参数（例如目标 ModItemViewModel）。</summary>
    public object? CommandParameter { get; init; }

    /// <summary>可选图标（UtilityIcon 的 Kind 名称，如 "Copy"、"Clean"）。</summary>
    public string? IconKind { get; }

    /// <summary>危险操作（删除等）用错误色显示。</summary>
    public bool IsDanger { get; init; }
}

/// <summary>
/// 统一的右键菜单模块：负责把条目定义渲染成 FluentAvalonia 菜单并在指针处打开，
/// 命令直接引用实例而非弹层内绑定，保证任何页面都能稳定弹出。
/// 用法：AppContextMenu.Show(new List&lt;object&gt; { new ContextMenuItem(...), AppContextMenu.Separator, ... }, placementTarget);
/// </summary>
public static class AppContextMenu
{
    /// <summary>占位分隔线，传入条目列表即可。</summary>
    public static readonly object Separator = new();

    /// <summary>构建并在 placementTarget 的指针位置显示右键菜单。</summary>
    public static void Show(IReadOnlyList<object> entries, Control placementTarget)
    {
        // ContextRequested 与列表的右键松开兜底可能处理同一次手势。
        // 已打开的菜单不再关闭重建，避免弹层和焦点在一帧内反复切换。
        if (placementTarget.ContextFlyout is FAMenuFlyout { IsOpen: true }) return;

        var menu = new FAMenuFlyout();
        foreach (var entry in entries)
        {
            if (ReferenceEquals(entry, Separator))
            {
                menu.Items.Add(new FAMenuFlyoutSeparator());
                continue;
            }

            if (entry is not ContextMenuItem definition) continue;

            var menuItem = new FAMenuFlyoutItem { Text = definition.Header, Command = definition.Command, CommandParameter = definition.CommandParameter };
            menuItem.Bind(FAMenuFlyoutItem.TextProperty, UiLocalization.Current.TextBinding(definition.Header));
            if (definition.IconKind is { } iconKind)
            {
                menuItem.IconSource = new FASymbolIconSource { Symbol = new UtilityIcon { Kind = iconKind }.Symbol };
            }
            if (definition.IsDanger)
            {
                menuItem.Bind(FAMenuFlyoutItem.ForegroundProperty, menuItem.GetResourceObservable("ErrorBrush"));
            }
            menu.Items.Add(menuItem);
        }

        placementTarget.ContextFlyout?.Hide();
        placementTarget.ContextFlyout = menu;
        menu.ShowAt(placementTarget, showAtPointer: true);
    }
}
