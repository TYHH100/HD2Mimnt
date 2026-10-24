namespace HD2Mimnt.UI.Services;

/// <summary>两页共用的批量选择规则；聚焦不等于选中，拖动由视图独立处理。</summary>
internal sealed class ModSelection<T>(
    Func<IList<T>> visibleItems,
    Func<IEnumerable<T>> selectedItems,
    Action<IEnumerable<T>, IEnumerable<T>> applySelection,
    Action<T, bool> setFocusMarker,
    Func<T, bool>? canSelect = null) where T : class
{
    private T? _anchor;
    private T? _focusedItem;

    private bool CanSelect(T item) => canSelect?.Invoke(item) ?? true;

    public void SetAnchor(T item)
    {
        if (CanSelect(item) && visibleItems().Contains(item)) _anchor = item;
    }

    public void RemoveMissingAnchor(ICollection<T> items)
    {
        if (_anchor != null && !items.Contains(_anchor)) _anchor = null;
        if (_focusedItem != null && !items.Contains(_focusedItem)) ClearFocusMarker();
    }

    public bool Focus(T item)
    {
        if (!CanSelect(item) || !visibleItems().Contains(item)) return false;
        Clear();
        _anchor = item;
        _focusedItem = item;
        setFocusMarker(item, true);
        return true;
    }

    public bool Toggle(T item)
    {
        if (!CanSelect(item) || !visibleItems().Contains(item)) return false;
        var selected = selectedItems().Contains(item);
        ClearFocusMarker();
        applySelection(selected ? [] : [item], selected ? [item] : []);
        _anchor = item;
        return true;
    }

    public bool SelectRange(T item, bool additive)
    {
        if (!CanSelect(item)) return false;
        var visible = visibleItems();
        var targetIndex = visible.IndexOf(item);
        if (targetIndex < 0) return false;
        var anchorIndex = _anchor == null ? targetIndex : visible.IndexOf(_anchor);
        if (anchorIndex < 0) anchorIndex = targetIndex;
        _anchor = visible[anchorIndex];
        var start = Math.Min(anchorIndex, targetIndex);
        var range = visible.Skip(start).Take(Math.Abs(anchorIndex - targetIndex) + 1)
            .Where(CanSelect).ToArray();
        ClearFocusMarker();
        applySelection(range, additive ? [] : selectedItems().ToArray());
        return true;
    }

    public void Clear()
    {
        _anchor = null;
        ClearFocusMarker();
        applySelection([], selectedItems().ToArray());
    }

    private void ClearFocusMarker()
    {
        if (_focusedItem != null) setFocusMarker(_focusedItem, false);
        _focusedItem = null;
    }

    public void SelectAll(T? focusedItem)
    {
        var visible = visibleItems().Where(CanSelect).ToArray();
        ClearFocusMarker();
        applySelection(visible, []);
        _anchor ??= focusedItem != null && visible.Contains(focusedItem) ? focusedItem : visible.FirstOrDefault();
    }

    public void ToggleAll()
    {
        var visible = visibleItems().Where(CanSelect).ToArray();
        var selected = selectedItems().ToHashSet();
        ClearFocusMarker();
        if (visible.All(selected.Contains)) applySelection([], visible);
        else applySelection(visible, []);
    }

    public void Invert()
    {
        var visible = visibleItems().Where(CanSelect).ToArray();
        var selected = selectedItems().ToHashSet();
        ClearFocusMarker();
        applySelection(visible.Where(item => !selected.Contains(item)).ToArray(),
            visible.Where(selected.Contains).ToArray());
    }
}
