using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ProfileView : UserControl
{
    private const double DragThreshold = 4;

    private ProfileItemViewModel? _dragProfile;
    private PointerPressedEventArgs? _pressedArgs;
    private Point _dragStartPoint;

    // 模组卡片拖拽状态（多选：拖动已选中卡片时整体移动选中集）
    private ProfileModViewModel? _pressedMod;
    private PointerPressedEventArgs? _modPressedArgs;
    private Point _modDragStartPoint;

    public ProfileView()
    {
        InitializeComponent();
    }

    /// <summary>右键配置文件行：经统一的 AppContextMenu 模块弹出操作菜单。</summary>
    private void ProfileList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ProfileViewModel) return;

        ProfileItemViewModel? profile = null;
        if (e.Source is Visual source)
        {
            var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            profile = item?.DataContext as ProfileItemViewModel;
        }
        profile ??= ProfileList.SelectedItem as ProfileItemViewModel;
        if (profile == null) return;

        var entries = new List<object>();
        if (!profile.IsActive)
        {
            entries.Add(new ContextMenuItem("设为当前", profile.ActivateCommand, "Check"));
        }
        entries.Add(new ContextMenuItem("复制配置文件", profile.DuplicateCommand, "Copy"));
        entries.Add(new ContextMenuItem("重命名", profile.RenameCommand, "Edit"));
        entries.Add(AppContextMenu.Separator);
        entries.Add(new ContextMenuItem("删除配置文件", profile.DeleteCommand, "Clean") { IsDanger = true });

        AppContextMenu.Show(entries, ProfileList);
        e.Handled = true;
    }

    private void ProfileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragProfile = null;
        _pressedArgs = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        // 命中的是哪个配置文件行
        var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not ProfileItemViewModel profile) return;

        _dragProfile = profile;
        _pressedArgs = e;
        _dragStartPoint = e.GetPosition(ProfileList);
    }

    private async void ProfileList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragProfile == null || _pressedArgs == null) return;

        if (!e.GetCurrentPoint(ProfileList).Properties.IsLeftButtonPressed)
        {
            _dragProfile = null;
            _pressedArgs = null;
            return;
        }

        var position = e.GetPosition(ProfileList);
        var delta = position - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        var profile = _dragProfile;
        var pressedArgs = _pressedArgs;
        _dragProfile = null;
        _pressedArgs = null;

        // Avalonia 12 拖放 API：DataTransfer + DoDragDropAsync
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(profile.Id.ToString()));
        await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
    }

    private void ProfileList_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetText() is null || !Guid.TryParse(e.DataTransfer.TryGetText(), out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
    }

    private void ProfileList_Drop(object? sender, DragEventArgs e)
    {
        var idText = e.DataTransfer.TryGetText();
        if (idText is null
            || !Guid.TryParse(idText, out var profileId)
            || DataContext is not ProfileViewModel viewModel)
        {
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        _ = viewModel.MoveProfileAsync(profileId, ComputeInsertIndex(e.GetPosition(ProfileList).Y));
    }

    /// <summary>根据落点 Y 坐标换算插入位置（行中点之前算插入到该行前）。</summary>
    private int ComputeInsertIndex(double y)
    {
        for (var index = 0; index < ProfileList.ItemCount; index++)
        {
            if (ProfileList.ContainerFromIndex(index) is not Control container) continue;
            var topLeft = container.TranslatePoint(new Point(0, 0), ProfileList);
            if (topLeft is { } point && y < point.Y + container.Bounds.Height / 2)
            {
                return index;
            }
        }

        return ProfileList.ItemCount;
    }

    /// <summary>
    /// 模组卡片按下：Ctrl+点击切换多选标记；普通按下时若未选中则清空其他选中（准备单卡拖动）。
    /// </summary>
    private void ModCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedMod = null;
        _modPressedArgs = null;
        if (sender is not Border card || card.DataContext is not ProfileModViewModel mod) return;
        if (DataContext is not ProfileViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 命中开关/按钮等交互控件时不进入选择与拖拽逻辑
        if (IsInteractiveSource(e.Source as Visual, card)) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            mod.IsSelected = !mod.IsSelected;
            e.Handled = true;
            return;
        }

        if (!mod.IsSelected)
        {
            foreach (var selected in vm.ProfileMods.Where(m => m.IsSelected))
            {
                selected.IsSelected = false;
            }
            mod.IsSelected = true;
        }

        _pressedMod = mod;
        _modPressedArgs = e;
        _modDragStartPoint = e.GetPosition(this);
    }

    /// <summary>模组卡片拖动启动：把拖动集（选中集或单卡）以 "id;id;…" 文本放入 DataTransfer。</summary>
    private async void ModCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedMod == null || _modPressedArgs == null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedMod = null;
            _modPressedArgs = null;
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _modDragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        if (DataContext is not ProfileViewModel vm) return;
        var movingIds = _pressedMod.IsSelected
            ? vm.ProfileMods.Where(m => m.IsSelected).Select(m => m.ModId).ToList()
            : new List<Guid> { _pressedMod.ModId };

        var pressedArgs = _modPressedArgs;
        _pressedMod = null;
        _modPressedArgs = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(string.Join(';', movingIds)));
        await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
    }

    private void ModList_DragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetMovingModIds(e, out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
    }

    private void ModList_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProfileViewModel vm || !TryGetMovingModIds(e, out var movingIds)) return;

        var insertIndex = ComputeModInsertIndex(e.GetPosition(ModItemsControl).Y);
        var anchor = insertIndex < ModItemsControl.ItemCount && ModItemsControl.ContainerFromIndex(insertIndex) is Control { DataContext: ProfileModViewModel target }
            ? target.ModId
            : (Guid?)null;

        e.DragEffects = DragDropEffects.Move;
        _ = vm.MoveModsAsync(movingIds, anchor);
    }

    /// <summary>根据落点 Y 坐标换算卡片列表的插入位置（未虚拟化到的区域视为末尾）。</summary>
    private int ComputeModInsertIndex(double y)
    {
        for (var index = 0; index < ModItemsControl.ItemCount; index++)
        {
            if (ModItemsControl.ContainerFromIndex(index) is not Control container) continue;
            var topLeft = container.TranslatePoint(new Point(0, 0), ModItemsControl);
            if (topLeft is { } point && y < point.Y + container.Bounds.Height / 2)
            {
                return index;
            }
        }

        return ModItemsControl.ItemCount;
    }

    private static bool TryGetMovingModIds(DragEventArgs e, out List<Guid> movingIds)
    {
        movingIds = new List<Guid>();
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var part in text.Split(';'))
        {
            if (Guid.TryParse(part, out var id))
            {
                movingIds.Add(id);
            }
        }

        return movingIds.Count > 0;
    }

    /// <summary>子选项行点击：同卡片内互斥多选一，选中后立即持久化。</summary>
    private void SubChoice_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: ProfileModSubOptionChoiceViewModel choice }
            && DataContext is ProfileViewModel { ModOptionsDialog: { } dialog })
        {
            dialog.SelectSubOption(choice);
        }
    }

    /// <summary>v0 互斥选项行点击：列表内单选，选中后立即持久化。</summary>
    private void LegacyChoice_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: ProfileModLegacyChoiceViewModel choice }
            && DataContext is ProfileViewModel { ModOptionsDialog: { } dialog })
        {
            dialog.SelectLegacyChoice(choice);
        }
    }

    /// <summary>
    /// 点击图片（模组图标/选项图/子选项图）：在主窗口顶层的覆盖层预览原图。
    /// 标记 Handled，子选项图上的点击只预览、不改变选中变体。
    /// </summary>
    private void PreviewImage_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ProfileViewModel vm) return;
        if (sender is not Border { DataContext: { } data }) return;

        switch (data)
        {
            case ProfileModOptionCardViewModel card:
                vm.ShowImagePreview(card.Image, card.Name);
                break;
            case ProfileModSubOptionChoiceViewModel sub:
                vm.ShowImagePreview(sub.Image, sub.Name);
                break;
            case ProfileModViewModel mod:
                vm.ShowImagePreview(mod.Icon, mod.ModName);
                break;
        }
    }

    /// <summary>判断按下起点是否落在卡片内的交互控件（开关/按钮/复选框/下拉）上。</summary>
    private static bool IsInteractiveSource(Visual? source, Visual card)
    {
        for (Visual? current = source; current != null && !ReferenceEquals(current, card); current = current.GetVisualParent())
        {
            if (current is ToggleSwitch or Button or CheckBox or ComboBox)
            {
                return true;
            }
        }

        return false;
    }
}
