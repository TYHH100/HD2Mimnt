using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HD2Mimnt.UI.ViewModels.Darktide;

namespace HD2Mimnt.UI.Views;

public partial class DarktideLoadOrderView : UserControl
{
    private string? _dragSourceName;
    private Control? _dimmedSource;

    public DarktideLoadOrderView() => InitializeComponent();

    private async void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DarktideLoadOrderItemViewModel item } || !item.CanMove) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(item.InternalName));
        BeginDragPreview(item.InternalName);
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            // 取消、拖出窗口和落下都走这里，源卡片不能留在半透明状态。
            ClearDragPreview();
        }
    }

    private void ActiveList_DragOver(object? sender, DragEventArgs e)
    {
        var sourceName = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(sourceName)) return;
        _dragSourceName ??= sourceName;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        ShowInsertionMarker(ComputeInsertionSlot(e.GetPosition(ActiveList).Y));
    }

    private void ActiveList_DragLeave(object? sender, DragEventArgs e)
    {
        // DragLeave 会从卡片内的文字和按钮冒泡，指针仍在列表视口内时保留预览，否则虚影会闪烁。
        if (new Rect(ActiveList.Bounds.Size).Contains(e.GetPosition(ActiveList))) return;
        InsertionMarker.IsVisible = false;
    }

    private async void ActiveList_Drop(object? sender, DragEventArgs e)
    {
        var sourceName = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(sourceName) || DataContext is not DarktideLoadOrderViewModel viewModel) return;
        e.Handled = true;
        var slot = ComputeInsertionSlot(e.GetPosition(ActiveList).Y);
        await viewModel.MoveBeforeAsync(sourceName, ResolveAnchorName(slot, sourceName));
    }

    private int ComputeInsertionSlot(double listY)
    {
        for (var index = 0; index < ActiveList.ItemCount; index++)
        {
            if (ActiveList.ContainerFromIndex(index) is not Control container) continue;
            if (container.TranslatePoint(new Point(0, 0), ActiveList) is not { } top) continue;
            if (listY < top.Y + container.Bounds.Height / 2) return index;
        }
        return ActiveList.ItemCount;
    }

    /// <summary>落点锚点与 MoveBeforeAsync 共用同一换算：源项先被移除，所以锚点取落点后第一个非源项。</summary>
    private string? ResolveAnchorName(int slot, string sourceName)
    {
        if (DataContext is not DarktideLoadOrderViewModel viewModel) return null;
        for (var index = slot; index < viewModel.ActiveItems.Count; index++)
        {
            var name = viewModel.ActiveItems[index].InternalName;
            if (!string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    private void BeginDragPreview(string sourceName)
    {
        _dragSourceName = sourceName;
        for (var index = 0; index < ActiveList.ItemCount; index++)
        {
            if (ActiveList.ContainerFromIndex(index) is not Control container
                || container.DataContext is not DarktideLoadOrderItemViewModel item) continue;
            if (!string.Equals(item.InternalName, sourceName, StringComparison.OrdinalIgnoreCase)) continue;
            _dimmedSource = container;
            container.Opacity = 0.35;
            break;
        }
    }

    private void ShowInsertionMarker(int slot)
    {
        var (container, after) = ResolveMarkerBoundary(slot);
        if (container?.TranslatePoint(new Point(0, after ? container.Bounds.Height : 0), PreviewLayer) is not { } edge)
        {
            InsertionMarker.IsVisible = false;
            return;
        }
        var left = ActiveList.TranslatePoint(default, PreviewLayer)?.X ?? 0;
        InsertionMarker.Width = Math.Max(20, ActiveList.Bounds.Width);
        Canvas.SetLeft(InsertionMarker, left);
        Canvas.SetTop(InsertionMarker, edge.Y - (after ? 1 : 2));
        InsertionMarker.IsVisible = true;
    }

    private (Control? Container, bool After) ResolveMarkerBoundary(int slot)
    {
        Control? last = null;
        for (var index = 0; index < ActiveList.ItemCount; index++)
        {
            if (ActiveList.ContainerFromIndex(index) is not Control container) continue;
            last = container;
            if (index < slot) continue;
            // 只有落点正好压在拖动源上时才顺延到下一张卡片：那里等于位置不变。
            if (container.DataContext is DarktideLoadOrderItemViewModel item
                && string.Equals(item.InternalName, _dragSourceName, StringComparison.OrdinalIgnoreCase)
                && index + 1 < ActiveList.ItemCount
                && ActiveList.ContainerFromIndex(index + 1) is Control next) return (next, false);
            return (container, false);
        }
        return (last, true);
    }

    private void ClearDragPreview()
    {
        if (_dimmedSource != null) _dimmedSource.Opacity = 1;
        _dimmedSource = null;
        _dragSourceName = null;
        InsertionMarker.IsVisible = false;
    }
}
