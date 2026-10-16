using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ModCreationView
{
    private ModCreationViewModel? _sortingModel;
    private IPointer? _sortingPointer;
    private ItemsControl? _sortingList;
    private ScrollViewer? _sortingScroll;
    private object? _sortingItem;
    private ModCreationOptionViewModel? _sortingParent;
    private Point _sortingStart;
    private bool _isSorting;
    private readonly List<(Control Control, double Opacity)> _sortingSources = [];

    private void InitializeSorting()
    {
        AddHandler(PointerPressedEvent, SortPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, SortMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, SortReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, SortWheelChanged, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => ClearSorting();
        DataContextChanged += (_, _) => AttachSortingModel(Model);
        AttachedToVisualTree += (_, _) => AttachSortingModel(Model);
        DetachedFromVisualTree += (_, _) => AttachSortingModel(null);
        CreationTabs.SelectionChanged += (_, args) =>
        { if (ReferenceEquals(args.Source, CreationTabs)) ClearSorting(); };
    }

    private void AttachSortingModel(ModCreationViewModel? model)
    {
        ClearSorting();
        if (_sortingModel != null)
        {
            _sortingModel.SortCancellationRequested -= CancelSorting;
            _sortingModel.PropertyChanged -= SortingModelChanged;
        }
        _sortingModel = model;
        if (_sortingModel != null)
        {
            _sortingModel.SortCancellationRequested += CancelSorting;
            _sortingModel.PropertyChanged += SortingModelChanged;
        }
    }

    private void CancelSorting(object? sender, EventArgs args) => ClearSorting();
    private void SortingModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ModCreationViewModel.SelectedOption) or nameof(ModCreationViewModel.CanEdit)) ClearSorting();
    }

    private void SortPressed(object? sender, PointerPressedEventArgs args)
    {
        if (Model is not { CanEdit: true } model || args.Source is not Visual source
            || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        var handle = ancestors.FirstOrDefault(control => control.Classes.Contains("creation-sort-handle"));
        var row = ancestors.OfType<ListBoxItem>().FirstOrDefault();
        var item = handle?.DataContext ?? row?.DataContext;
        ItemsControl? list;
        ScrollViewer? scroll;
        ModCreationOptionViewModel? parent = null;
        if (item is ModCreationOptionViewModel option && model.Options.Contains(option)
            && ancestors.Contains(CreationOptionList))
        {
            if (handle == null && ancestors.Any(control => control is TextBox or Button or CheckBox)) return;
            model.SelectedOption = option;
            list = CreationOptionList;
            scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        }
        else if (handle != null && item is ModCreationSubOptionViewModel child
            && model.SelectedOption is { } selected && selected.SubOptions.Contains(child))
        {
            parent = selected;
            list = ancestors.OfType<ItemsControl>().FirstOrDefault(control => control.Name == "CreationSubOptionList");
            scroll = CreationOptionsScroll;
        }
        else return;
        if (list == null || scroll == null) return;
        ClearSorting();
        _sortingItem = item; _sortingList = list; _sortingScroll = scroll; _sortingParent = parent;
        _sortingStart = args.GetPosition(this); _sortingPointer = args.Pointer;
        args.Pointer.Capture(this);
        args.Handled = true;
    }

    private void SortMoved(object? sender, PointerEventArgs args)
    {
        if (!ReferenceEquals(_sortingPointer, args.Pointer)) return;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ClearSorting(); return; }
        var position = args.GetPosition(this);
        var delta = position - _sortingStart;
        if (!_isSorting && Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        _isSorting = true;
        UpdateSortPreview(position);
        args.Handled = true;
    }

    // 预览与提交共用插入槽位；这里只处理位置，松开前不修改草稿集合。
    private bool TryGetSortSlot(Point position, out int slot, out double edge)
    {
        slot = -1; edge = 0;
        if (!_isSorting || _sortingList == null || _sortingScroll == null || Model?.CanEdit != true
            || this.TranslatePoint(position, _sortingScroll) is not { } inViewport
            || !new Rect(_sortingScroll.Bounds.Size).Contains(inViewport)
            || this.TranslatePoint(position, _sortingList) is not { } inList
            || inList.X < 0 || inList.X > _sortingList.Bounds.Width) return false;
        for (var index = 0; index < _sortingList.ItemCount; index++)
        {
            if (_sortingList.ContainerFromIndex(index) is not Control container
                || container.TranslatePoint(default, _sortingList) is not { } top) continue;
            var before = inList.Y < top.Y + container.Bounds.Height / 2;
            if (container.TranslatePoint(new Point(0, before ? 0 : container.Bounds.Height), CreationSortLayer) is not { } boundary) continue;
            slot = before ? index : index + 1; edge = boundary.Y;
            if (before) break;
        }
        return slot >= 0;
    }

    private void UpdateSortPreview(Point position)
    {
        RestoreSortingSources();
        if (_sortingList == null) return;
        foreach (var container in _sortingList.GetRealizedContainers())
        {
            if (!ReferenceEquals(container.DataContext, _sortingItem)) continue;
            _sortingSources.Add((container, container.Opacity)); container.Opacity *= 0.35;
        }
        if (!TryGetSortSlot(position, out _, out var edge)
            || _sortingScroll?.TranslatePoint(default, CreationSortLayer) is not { } viewport
            || _sortingList.TranslatePoint(default, CreationSortLayer) is not { } origin)
        { CreationSortMarker.IsVisible = false; return; }
        CreationSortMarker.Width = Math.Max(1, Math.Min(_sortingList.Bounds.Width, _sortingScroll.Bounds.Width) - 8);
        Canvas.SetLeft(CreationSortMarker, origin.X + 4);
        Canvas.SetTop(CreationSortMarker, Math.Clamp(edge, viewport.Y + 2,
            Math.Max(viewport.Y + 2, viewport.Y + _sortingScroll.Bounds.Height - 3)));
        CreationSortMarker.IsVisible = true;
    }

    private void SortReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!ReferenceEquals(_sortingPointer, args.Pointer) || args.InitialPressMouseButton != MouseButton.Left) return;
        var valid = TryGetSortSlot(args.GetPosition(this), out var slot, out _);
        var item = _sortingItem; var parent = _sortingParent; var model = Model;
        ClearSorting();
        if (valid && model != null)
        {
            if (item is ModCreationOptionViewModel option) model.MoveOption(option, slot);
            else if (item is ModCreationSubOptionViewModel sub && parent != null) model.MoveSubOption(parent, sub, slot);
        }
        args.Handled = true;
    }

    private void SortWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        if (_sortingPointer == null || _sortingScroll == null) return;
        args.Handled = true;
        var position = args.GetPosition(this);
        if (this.TranslatePoint(position, _sortingScroll) is not { } point
            || !new Rect(_sortingScroll.Bounds.Size).Contains(point)) return;
        _sortingScroll.SetCurrentValue(ScrollViewer.OffsetProperty,
            _sortingScroll.Offset - new Vector(0, args.Delta.Y * Math.Max(16, _sortingScroll.SmallChange.Height) * 3));
        var pointer = _sortingPointer;
        Dispatcher.UIThread.Post(() =>
        { if (_isSorting && ReferenceEquals(_sortingPointer, pointer)) UpdateSortPreview(position); }, DispatcherPriority.Loaded);
    }

    private void RestoreSortingSources()
    {
        foreach (var (control, opacity) in _sortingSources) control.Opacity = opacity;
        _sortingSources.Clear();
    }

    private void ClearSorting()
    {
        var pointer = _sortingPointer;
        _sortingPointer = null; _sortingItem = null; _sortingParent = null; _sortingList = null; _sortingScroll = null; _isSorting = false;
        CreationSortMarker.IsVisible = false;
        RestoreSortingSources();
        if (ReferenceEquals(pointer?.Captured, this)) pointer.Capture(null);
    }
}
