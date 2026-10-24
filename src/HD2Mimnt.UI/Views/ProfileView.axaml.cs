using HD2Mimnt.UI.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    private readonly List<(Control Control, double Opacity)> _dimmedDragSources = new();
    private ProfileViewModel? _navigationViewModel;
    private IPointer? _sortPointer;
    private ItemsControl? _sortList;
    private DataTransfer? _sortData;
    private Func<object?, bool>? _sortIsMoving;

    private void SubscribeConflictNavigation(ProfileViewModel? viewModel)
    {
        if (_navigationViewModel != null) _navigationViewModel.ConflictModLocationRequested -= OnConflictModLocationRequested;
        _navigationViewModel = viewModel;
        if (viewModel != null) viewModel.ConflictModLocationRequested += OnConflictModLocationRequested;
    }

    private void OnConflictModLocationRequested(object? sender, ProfileModViewModel target)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible || DataContext != sender || !target.IsConflictLocation) return;
            // 虚拟化面板负责实现目标与布局，不再对同一容器重复发起滚动。
            ModItemsControl.ScrollIntoView(target);
        }, DispatcherPriority.Loaded);
    }

    public ProfileView()
    {
        InitializeComponent();
        // 在 ListBoxItem 默认选择逻辑之前拦截右键，避免菜单操作自动激活目标配置。
        ProfileList.AddHandler(PointerPressedEvent, ProfileList_PointerPressed, RoutingStrategies.Tunnel);
        // 右键事件在列表容器统一捕获，并包含已被子控件处理过的事件。
        ModItemsControl.AddHandler(InputElement.PointerReleasedEvent, ModList_ContextReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, ModList_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SortPointer_Moved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, SortPointer_Released, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, SortPointer_WheelChanged, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) =>
        {
            if (_sortPointer != null && _sortPointer.Captured != this) ClearSortPreview();
        };
        ModItemsControl.AddHandler(PointerReleasedEvent, ModList_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) =>
        {
            ClearSortPreview();
            (DataContext as ProfileViewModel)?.CloseDetailPanelCommand.Execute(null);
            _navigationViewModel?.CloseConflictPickerCommand.Execute(null);
            SubscribeConflictNavigation(null);
        };
        AttachedToVisualTree += (_, _) => SubscribeConflictNavigation(DataContext as ProfileViewModel);
        DataContextChanged += (_, _) =>
        {
            ClearSortPreview();
            SubscribeConflictNavigation(DataContext as ProfileViewModel);
        };
    }

    /// <summary>右键配置文件行：经统一的 AppContextMenu 模块弹出操作菜单。</summary>
    private void ProfileList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ProfileViewModel) return;

        ProfileItemViewModel? profile = null;
        var hasPosition = e.TryGetPosition(ProfileList, out var position);
        var source = hasPosition ? ProfileList.InputHitTest(position) as Visual : e.Source as Visual;
        if (profile == null && source != null)
        {
            var item = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            profile = item?.DataContext as ProfileItemViewModel;
        }
        if (!hasPosition) profile ??= ProfileList.SelectedItem as ProfileItemViewModel;
        if (profile == null) return;

        var entries = new List<object>();
        if (!profile.IsActive)
        {
            entries.Add(new ContextMenuItem("设为当前", profile.ActivateCommand, "Check"));
        }
        entries.Add(new ContextMenuItem("复制配置文件", profile.DuplicateCommand, "Copy"));
        entries.Add(new ContextMenuItem("重命名", profile.RenameCommand, "Edit"));
        entries.Add(AppContextMenu.Separator);
        if (!profile.IsDefault)
            entries.Add(new ContextMenuItem("删除配置文件", profile.DeleteCommand, "Clean") { IsDanger = true });

        AppContextMenu.Show(entries, ProfileList);
        e.Handled = true;
    }

    /// <summary>右键模组卡片：保留当前多选集，或将未选中的目标切换为单选后打开定位菜单。</summary>
    private void ModCard_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border { DataContext: ProfileModViewModel mod } card
            || DataContext is not ProfileViewModel vm) return;

        ShowModContextMenu(card, mod, vm);
        e.Handled = true;
    }

    private static void ShowModContextMenu(Border card, ProfileModViewModel mod, ProfileViewModel vm)
    {
        if (mod.IsSeparator)
        {
            var entries = new List<object>
            {
                new ContextMenuItem(mod.IsUserSeparator ? "编辑分组名称与颜色" : "编辑分组颜色", vm.OpenRenameSeparatorCommand, "Edit") { CommandParameter = mod }
            };
            if (mod.IsUserSeparator)
                entries.Add(new ContextMenuItem("删除分组", vm.RemoveSeparatorCommand, "Clean", isDanger: true) { CommandParameter = mod });
            AppContextMenu.Show(entries, card);
            return;
        }

        var modEntries = new List<object>
        {
            new ContextMenuItem("查看详情", vm.ShowModDetailCommand, "Info")
            {
                CommandParameter = mod
            }
        };
        if (vm.Compatibility != null) modEntries.Add(new ContextMenuItem("兼容性检查与修复", vm.CheckModCompatibilityCommand, "Repair") { CommandParameter = mod });
        modEntries.Add(new ContextMenuItem("添加到其他配置文件…", vm.AddModToOtherProfilesCommand, "Add") { CommandParameter = mod });
        if (mod.CanDrag)
        {
            modEntries.Add(AppContextMenu.Separator);
            if (!mod.IsFixedGroupMember)
                modEntries.Add(new ContextMenuItem("在此处添加分组分隔符", vm.OpenAddSeparatorCommand, "Add")
                {
                    CommandParameter = mod
                });
            modEntries.Add(new ContextMenuItem("移动到指定位置…", vm.OpenMoveToPositionCommand, "Edit")
            {
                CommandParameter = mod
            });
        }
        modEntries.Add(AppContextMenu.Separator);
        modEntries.Add(new ContextMenuItem("打开文件位置", vm.OpenModLocationCommand, "Folder")
        {
            CommandParameter = mod
        });
        if (mod.SourceInfo is { HasPlatformLink: true } source)
            modEntries.Add(new ContextMenuItem("打开来源网页", source.OpenSourcePageCommand, "Globe"));
        if (mod.SourceInfo is { HasAuthorPage: true } author)
            modEntries.Add(new ContextMenuItem("打开作者主页", author.OpenAuthorPageCommand, "Globe"));
        modEntries.Add(AppContextMenu.Separator);
        modEntries.Add(new ContextMenuItem("删除模组文件", vm.DeleteModCommand, "Clean", isDanger: true)
        {
            CommandParameter = mod
        });
        AppContextMenu.Show(modEntries, card);
    }

    private void ProfileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragProfile = null;
        _pressedArgs = null;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        // 命中的是哪个配置文件行
        var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not ProfileItemViewModel profile) return;
        if (DataContext is ProfileViewModel vm && ReferenceEquals(profile, vm.Profiles.FirstOrDefault())) return;

        _dragProfile = profile;
        _pressedArgs = e;
        _dragStartPoint = e.GetPosition(ProfileList);
    }

    private void ProfileList_PointerMoved(object? sender, PointerEventArgs e)
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
        _dragProfile = null;
        _pressedArgs = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(profile.Id.ToString()));
        BeginProfileDragPreview(profile.Id);
        BeginSortPointerDrag(e, ProfileList, data);
    }

    private void ProfileList_DragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetMovingProfileId(e, out var profileId))
        {
            e.DragEffects = DragDropEffects.None;
            HideSortPreview();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        UpdateProfileDragPreview(profileId, e.GetPosition(ProfileList).Y);
        e.Handled = true;
    }

    private void ProfileList_Drop(object? sender, DragEventArgs e)
    {
        if (!TryGetMovingProfileId(e, out var profileId) || DataContext is not ProfileViewModel viewModel)
        {
            ClearSortPreview();
            return;
        }

        var (_, targetIndex) = GetProfileDropTarget(profileId, e.GetPosition(ProfileList).Y);
        ClearSortPreview();
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        _ = viewModel.MoveProfileAsync(profileId, targetIndex);
    }

    /// <summary>根据落点 Y 坐标换算插入位置（行中点之前算插入到该行前）。</summary>
    private int ComputeInsertIndex(double y)
        => ComputeInsertionSlot(ProfileList, y);

    /// <summary>
    /// Ctrl/Shift 共用模组库的选择规则；普通单击只聚焦，已选卡片延迟至松开清空以支持整组拖动。
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
        if (!mod.IsSeparator && e.ClickCount >= 2
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.ShowModDetailCommand.Execute(mod);
            e.Handled = true;
            return;
        }
        if (!mod.CanDrag) return;
        ModItemsControl.Focus();

        if (mod.IsUserSeparator)
        {
            _pressedMod = mod;
            _modPressedArgs = e;
            _modDragStartPoint = e.GetPosition(this);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.SelectModRange(mod, additive: e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ToggleModSelected(mod);
            e.Handled = true;
            return;
        }

        if (mod.IsSelected)
        {
            vm.SetModSelectionAnchor(mod);
            vm.FocusedMod = mod;
        }
        else vm.FocusMod(mod);

        if (!mod.CanDrag) return;
        _pressedMod = mod;
        _modPressedArgs = e;
        _modDragStartPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void ModList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _sortPointer != null)
        {
            ClearSortPreview();
            e.Handled = true;
            return;
        }
        if (e.Handled) return;
        if (!IsEffectivelyVisible || DataContext is not ProfileViewModel vm || vm.IsModOptionsDialogOpen
            || vm.Compatibility?.IsOpen == true || vm.IsMoveToPositionDialogOpen || vm.IsAddModOpen || vm.IsSeparatorDialogOpen || vm.IsConflictPickerOpen
            || vm.IsDetailPanelOpen) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main
            && (main.SelectedTabIndex != 0 || main.Tutorial.IsOpen || main.LibraryViewModel.IsDeploying)) return;
        if (e.Key == Key.Escape)
        {
            vm.ClearModSelectionCommand.Execute(null);
            _pressedMod = null;
            _modPressedArgs = null;
            ClearSortPreview();
            e.Handled = true;
            return;
        }
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .Any(control => control is TextBox or ComboBox)) return;
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.SelectAllVisibleMods();
            e.Handled = true;
        }
    }

    private void ModList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 按下时保留多选以支持整组拖动；未开始拖动的普通点击只聚焦并退出批量选择。
        if (e.InitialPressMouseButton == MouseButton.Left && _pressedMod is { } clicked
            && _modPressedArgs != null && DataContext is ProfileViewModel vm)
        {
            vm.FocusMod(clicked);
        }
        _pressedMod = null;
        _modPressedArgs = null;
    }

    private void ModBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || DataContext is not ProfileViewModel vm) return;
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .Any(control => control is ListBoxItem or Avalonia.Controls.Primitives.ScrollBar)) return;
        vm.ClearModSelectionCommand.Execute(null);
    }

    private void OnDetailBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ProfileViewModel vm) vm.CloseDetailPanelCommand.Execute(null);
        e.Handled = true;
    }

    private void ModList_ContextReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        if (e.Source is not Visual source) return;
        var card = new[] { source }.Concat(source.GetVisualAncestors()).OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("mod-card") || border.Classes.Contains("profile-separator"));
        if (card?.DataContext is not ProfileModViewModel mod
            || DataContext is not ProfileViewModel vm) return;

        ShowModContextMenu(card, mod, vm);
        e.Handled = true;
    }

    /// <summary>模组卡片拖动启动：把拖动集（选中集或单卡）以 "id;id;…" 文本放入 DataTransfer。</summary>
    private void ModCard_PointerMoved(object? sender, PointerEventArgs e)
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
        if (vm.ProfileMods.Any(mod => movingIds.Contains(mod.ModId) && !mod.CanDrag)) return;

        _pressedMod = null;
        _modPressedArgs = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(string.Join(';', movingIds)));
        BeginModDragPreview(movingIds);
        BeginSortPointerDrag(e, ModItemsControl, data);
    }

    // 页面内排序使用指针捕获，避免系统拖放循环接管输入后滚轮无法滚动列表。
    // 仍通过原有 DragOver/Drop 路径预览、校验及保存落点。
    private void BeginSortPointerDrag(PointerEventArgs e, ItemsControl list, DataTransfer data)
    {
        _sortList = list;
        _sortData = data;
        _sortPointer = e.Pointer;
        e.Pointer.Capture(this);
        UpdateSortPointerDrag(e);
        e.Handled = true;
    }

    private Control SortViewport => ReferenceEquals(_sortList, ProfileList) ? ProfileList : ModScrollViewer;

    private void UpdateSortPointerDrag(PointerEventArgs e)
    {
        if (_sortList == null || _sortData == null) return;
        // 滚动会回收卡片容器，透明度必须跟随当前数据，不能留在复用后的其他行上。
        if (_sortIsMoving is { } isMoving) DimDragSources(_sortList, isMoving);
        if (!new Rect(SortViewport.Bounds.Size).Contains(e.GetPosition(SortViewport)))
        {
            HideSortPreview();
            return;
        }
        _sortList.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, _sortData, _sortList,
            e.GetPosition(_sortList), e.KeyModifiers));
    }

    private void SortPointer_Moved(object? sender, PointerEventArgs e)
    {
        if (_sortPointer != e.Pointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) ClearSortPreview();
        else UpdateSortPointerDrag(e);
        e.Handled = true;
    }

    private void SortPointer_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_sortPointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        try
        {
            if (_sortList != null && _sortData != null
                && new Rect(SortViewport.Bounds.Size).Contains(e.GetPosition(SortViewport)))
                _sortList.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, _sortData, _sortList,
                    e.GetPosition(_sortList), e.KeyModifiers));
        }
        finally { ClearSortPreview(); }
        e.Handled = true;
    }

    private void SortPointer_WheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_sortPointer == null || _sortData == null) return;
        e.Handled = true;
        if (!new Rect(SortViewport.Bounds.Size).Contains(e.GetPosition(SortViewport))) return;
        var scroll = ReferenceEquals(_sortList, ProfileList)
            ? ProfileList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            : ModScrollViewer;
        if (scroll == null) return;
        scroll.SetCurrentValue(ScrollViewer.OffsetProperty,
            scroll.Offset - new Vector(0, e.Delta.Y * scroll.SmallChange.Height * 3));
        var data = _sortData;
        // 等虚拟化面板实现新视口的行后刷新插入线，鼠标无需再次移动。
        Dispatcher.UIThread.Post(() =>
        {
            if (_sortData == data) UpdateSortPointerDrag(e);
        }, DispatcherPriority.Loaded);
    }

    private void ModList_DragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetMovingModIds(e, out var movingIds))
        {
            e.DragEffects = DragDropEffects.None;
            HideSortPreview();
            return;
        }

        var (_, anchor) = GetModDropTarget(movingIds, e.GetPosition(ModItemsControl).Y);
        if (DataContext is not ProfileViewModel vm || !vm.CanMoveMods(movingIds, anchor))
        {
            e.DragEffects = DragDropEffects.None;
            HideSortPreview();
            e.Handled = true;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        UpdateModDragPreview(movingIds, e.GetPosition(ModItemsControl).Y);
        e.Handled = true;
    }

    private void ModList_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProfileViewModel vm || !TryGetMovingModIds(e, out var movingIds))
        {
            ClearSortPreview();
            return;
        }
        var (_, anchor) = GetModDropTarget(movingIds, e.GetPosition(ModItemsControl).Y);
        ClearSortPreview();

        if (!vm.CanMoveMods(movingIds, anchor))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        _ = vm.MoveModsAsync(movingIds, anchor);
    }

    /// <summary>根据落点 Y 坐标换算卡片列表的插入位置，使用当前已实现容器的边界。</summary>
    private int ComputeModInsertIndex(double y)
        => ComputeInsertionSlot(ModItemsControl, y);

    private static int ComputeInsertionSlot(ItemsControl list, double y)
    {
        var lastRealizedIndex = -1;
        for (var index = 0; index < list.ItemCount; index++)
        {
            if (list.ContainerFromIndex(index) is not Control container) continue;
            lastRealizedIndex = index;
            var topLeft = container.TranslatePoint(new Point(0, 0), list);
            if (topLeft is { } point && y < point.Y + container.Bounds.Height / 2)
            {
                return index;
            }
        }

        // 滚动后只实现部分容器，视口底部对应最后一个可见项之后，而不是全列表末尾。
        return lastRealizedIndex >= 0 ? lastRealizedIndex + 1 : list.ItemCount;
    }

    private bool TryGetMovingModIds(DragEventArgs e, out List<Guid> movingIds)
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

        return movingIds.Count > 0 && DataContext is ProfileViewModel vm
            && !vm.IsProfileModsLoading && movingIds.All(id => vm.ProfileMods.Concat(vm.ProfileSeparators)
                .Any(mod => mod.ModId == id && mod.CanDrag));
    }

    private bool TryGetMovingProfileId(DragEventArgs e, out Guid profileId)
    {
        profileId = default;
        if (!Guid.TryParse(e.DataTransfer.TryGetText(), out var parsedId)) return false;
        profileId = parsedId;
        return DataContext is ProfileViewModel vm
            && vm.Profiles.Skip(1).Any(profile => profile.Id == parsedId);
    }

    private (int Slot, int TargetIndex) GetProfileDropTarget(Guid profileId, double y)
    {
        var vm = (ProfileViewModel)DataContext!;
        var current = vm.Profiles.ToList().FindIndex(profile => profile.Id == profileId);
        var slot = Math.Clamp(ComputeInsertIndex(y), 1, ProfileList.ItemCount);
        // MoveProfileAsync 接受移除源项后的最终索引，预览与提交共用此换算。
        var target = Math.Clamp(slot > current ? slot - 1 : slot, 1, vm.Profiles.Count - 1);
        return (slot, target);
    }

    private (int Slot, Guid? Anchor) GetModDropTarget(IReadOnlyList<Guid> movingIds, double y)
    {
        var vm = (ProfileViewModel)DataContext!;
        var slot = ComputeModInsertIndex(y);
        var expandedIds = vm.ExpandMovingRows(movingIds);
        // 自身或多选中的卡片不是插入锚点，继续寻找下一个非拖动项。
        while (slot < vm.ProfileRows.Count && expandedIds.Contains(vm.ProfileRows[slot].ModId)) slot++;
        return (slot, slot < vm.ProfileRows.Count ? vm.ProfileRows[slot].ModId : null);
    }

    private void BeginProfileDragPreview(Guid profileId)
    {
        ClearSortPreview();
        DimDragSources(ProfileList, data => data is ProfileItemViewModel profile && profile.Id == profileId);
    }

    private void BeginModDragPreview(IReadOnlyList<Guid> movingIds)
    {
        ClearSortPreview();
        var expandedIds = ((ProfileViewModel)DataContext!).ExpandMovingRows(movingIds);
        DimDragSources(ModItemsControl, data => data is ProfileModViewModel mod && expandedIds.Contains(mod.ModId));
    }

    private void DimDragSources(ItemsControl list, Func<object?, bool> isMoving)
    {
        RestoreDragSources();
        _sortIsMoving = isMoving;
        for (var index = 0; index < list.ItemCount; index++)
        {
            if (list.ContainerFromIndex(index) is not Control container || !isMoving(container.DataContext)) continue;
            _dimmedDragSources.Add((container, container.Opacity));
            container.Opacity *= 0.35;
        }
    }

    private void UpdateProfileDragPreview(Guid profileId, double y)
    {
        if (DataContext is not ProfileViewModel vm) return;
        var profile = vm.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile == null || ReferenceEquals(profile, vm.Profiles.FirstOrDefault())) { HideSortPreview(); return; }
        var (slot, target) = GetProfileDropTarget(profileId, y);
        var destination = target == vm.Profiles.IndexOf(profile) ? "位置不变"
            : slot < vm.Profiles.Count ? $"松开放到「{vm.Profiles[slot].Name}」之前" : "松开放到列表末尾";
        ShowSortPreview(ProfileList, ProfileList, slot, profile.Name, destination);
    }

    private void UpdateModDragPreview(IReadOnlyList<Guid> movingIds, double y)
    {
        if (DataContext is not ProfileViewModel vm) return;
        var moving = vm.ProfileMods.Concat(vm.ProfileSeparators).Where(mod => movingIds.Contains(mod.ModId)).ToArray();
        if (moving.Length == 0) { HideSortPreview(); return; }
        var (slot, anchor) = GetModDropTarget(movingIds, y);
        if (!vm.CanMoveMods(movingIds, anchor)) { HideSortPreview(); return; }
        var title = moving.Length == 1 ? moving[0].IsUserSeparator
            ? $"{moving[0].ModName} · 整组 {moving[0].GroupModCount} 个模组" : moving[0].ModName
            : $"{moving[0].ModName} 等 {moving.Length} 个模组";
        var destination = anchor == Guid.Empty ? "松开放到 BSL 分隔符之前"
            : anchor is { } id ? $"松开放到「{vm.ProfileMods.Concat(vm.ProfileSeparators).First(mod => mod.ModId == id).ModName}」之前"
            : "松开放到列表末尾";
        ShowSortPreview(ModItemsControl, ModScrollViewer, slot, title, destination);
    }

    private void SeparatorMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProfileModViewModel mod } button
            || DataContext is not ProfileViewModel vm) return;
        var card = button.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("profile-separator"));
        if (card != null) ShowModContextMenu(card, mod, vm);
        e.Handled = true;
    }

    private void ShowSortPreview(ItemsControl list, Control viewport, int slot, string title, string destination)
    {
        if (viewport.TranslatePoint(default, SortPreviewLayer) is not { } viewportOrigin) return;
        Control? boundary = null;
        var after = false;
        for (var index = 0; index < list.ItemCount; index++)
        {
            if (list.ContainerFromIndex(index) is not Control container) continue;
            boundary = container;
            after = index < slot;
            if (!after) break;
        }
        if (boundary?.TranslatePoint(new Point(0, after ? boundary.Bounds.Height : 0), SortPreviewLayer) is not { } edge)
        {
            HideSortPreview();
            return;
        }
        var width = Math.Max(20, Math.Min(list.Bounds.Width - 8, viewport.Bounds.Width - 8));
        var top = viewportOrigin.Y + 2;
        var bottom = viewportOrigin.Y + viewport.Bounds.Height - 3;
        var markerY = Math.Clamp(edge.Y, top, Math.Max(top, bottom));
        SortInsertionMarker.Width = width;
        Canvas.SetLeft(SortInsertionMarker, viewportOrigin.X + 4);
        Canvas.SetTop(SortInsertionMarker, markerY);
        SortDragTitle.Bind(TextBlock.TextProperty, UiLocalization.Current.TextBinding(title));
        SortDragDestination.Bind(TextBlock.TextProperty, UiLocalization.Current.TextBinding(destination));
        SortDragGhost.Width = Math.Min(width, 360);
        SortInsertionMarker.IsVisible = true;
        SortDragGhost.IsVisible = true;
        SortDragGhost.Measure(new Size(SortDragGhost.Width, double.PositiveInfinity));
        var ghostHeight = SortDragGhost.DesiredSize.Height;
        var ghostY = markerY + 8;
        if (ghostY + ghostHeight > bottom) ghostY = markerY - ghostHeight - 8;
        Canvas.SetLeft(SortDragGhost, viewportOrigin.X + 4);
        Canvas.SetTop(SortDragGhost, Math.Clamp(ghostY, top, Math.Max(top, bottom - ghostHeight)));
    }

    private void SortList_DragLeave(object? sender, DragEventArgs e)
    {
        // DragLeave 会从卡片内的文字/图片/按钮冒泡。仍在列表视口内时保留虚影，
        // 否则随后到来的 DragOver 会反复隐藏/显示预览，造成闪烁。
        var viewport = ReferenceEquals(sender, ProfileList) ? (Control)ProfileList : ModScrollViewer;
        if (new Rect(viewport.Bounds.Size).Contains(e.GetPosition(viewport))) return;
        HideSortPreview();
    }

    private void HideSortPreview()
    {
        SortInsertionMarker.IsVisible = false;
        SortDragGhost.IsVisible = false;
    }

    private void ClearSortPreview()
    {
        var pointer = _sortPointer;
        _sortPointer = null;
        _sortList = null;
        (_sortData as IDisposable)?.Dispose();
        _sortData = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        HideSortPreview();
        RestoreDragSources();
        _sortIsMoving = null;
        _dragProfile = null;
        _pressedArgs = null;
        _pressedMod = null;
        _modPressedArgs = null;
    }

    private void RestoreDragSources()
    {
        foreach (var (control, opacity) in _dimmedDragSources) control.Opacity = opacity;
        _dimmedDragSources.Clear();
    }

    /// <summary>
    /// 点击图片（模组图标/选项图/子选项图）：在主窗口顶层的覆盖层预览原图。
    /// 标记 Handled，子选项图上的点击只预览、不改变选中变体。
    /// </summary>
    private async void PreviewImage_Tapped(object? sender, TappedEventArgs e)
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
                await vm.ShowModImagePreviewAsync(mod.ModId, mod.ModName);
                break;
        }
    }

    /// <summary>判断按下起点是否落在卡片内的交互控件（开关/按钮/复选框/下拉）上。</summary>
    private static bool IsInteractiveSource(Visual? source, Visual card)
    {
        for (Visual? current = source; current != null && !ReferenceEquals(current, card); current = current.GetVisualParent())
        {
            if (current is ToggleSwitch or Button or CheckBox or ComboBox or TextBox)
            {
                return true;
            }
        }

        return false;
    }
}
