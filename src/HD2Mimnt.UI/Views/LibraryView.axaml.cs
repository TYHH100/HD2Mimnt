using HD2Mimnt.UI.Localization;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnModListKeyDown, RoutingStrategies.Tunnel);
        ModList.AddHandler(PointerPressedEvent, OnModListPointerPressed, RoutingStrategies.Tunnel);
        ModList.AddHandler(PointerReleasedEvent, OnModListContextReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        DetachedFromVisualTree += async (_, _) =>
        {
            if (DataContext is LibraryViewModel vm)
            {
                vm.ModLink?.CloseCommand.Execute(null);
                if (vm.ModOnline is { } online) await online.CloseCommand.ExecuteAsync(null);
            }
        };
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        if (vm.IsAddToProfileDialogOpen || vm.Import.IsOpen || vm.ModLink?.IsOpen == true || vm.ModOnline?.IsOpen == true) return;
        var isTextInput = e.Source is Visual source &&
            (source is TextBox || source.GetVisualAncestors().Any(ancestor => ancestor is TextBox));
        var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (controlPressed && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (isTextInput) return;

        if (controlPressed && e.Key == Key.A)
        {
            vm.SelectAllVisibleCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            await vm.BatchDeleteCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.OpenFocusedModCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.IsDetailPanelOpen)
            {
                vm.CloseDetailPanelCommand.Execute(null);
            }
            else
            {
                vm.ClearSelectionCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private async void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            await vm.ExecuteSearchCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 卡片点击：双击打开详情；Ctrl+点击切换多选；Shift+点击范围选择；
    /// 普通单击只聚焦并清空多选；在隧道路由中处理，避免原生列表再次改写选择。
    /// </summary>
    private void OnModListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.GetVisualAncestors().Prepend(source).OfType<ListBoxItem>().FirstOrDefault() is not { } container
            || container.DataContext is not ModItemViewModel mod
            || DataContext is not LibraryViewModel vm)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (IsActionSource(source, container)) return;

        ModList.Focus();

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.ClickCount >= 2 && !control && !shift)
        {
            vm.ShowModDetailCommand.Execute(mod);
            e.Handled = true;
            return;
        }

        if (shift)
        {
            vm.SelectRange(mod, additive: control);
            e.Handled = true;
        }
        else if (control)
        {
            vm.ToggleSelected(mod);
            e.Handled = true;
        }
        else
        {
            vm.FocusMod(mod);
            e.Handled = true;
        }
    }

    private static bool IsActionSource(Visual? source, Visual card)
    {
        for (var current = source; current != null && current != card; current = current.GetVisualParent())
            if (current is Button or TextBox or ToggleSwitch or CheckBox or ComboBox) return true;
        return false;
    }

    private void OnModListBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || DataContext is not LibraryViewModel vm) return;
        if (e.Source is Visual source && (source is ListBoxItem ||
            source.GetVisualAncestors().Any(ancestor => ancestor is ListBoxItem or Avalonia.Controls.Primitives.ScrollBar))) return;
        vm.ClearSelectionCommand.Execute(null);
    }

    private void OnModListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Visual eventSource ||
            !eventSource.GetVisualAncestors().Prepend(eventSource).Contains(ModList)) return;
        if (DataContext is not LibraryViewModel vm || vm.FilteredMods.Count == 0 ||
            vm.IsAddToProfileDialogOpen || vm.IsDetailPanelOpen || vm.Import.IsOpen || vm.ModLink?.IsOpen == true || vm.ModOnline?.IsOpen == true) return;
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source).Any(visual => visual is Button or TextBox)) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var index = vm.FocusedMod == null ? -1 : vm.FilteredMods.IndexOf(vm.FocusedMod);
        var targetIndex = e.Key switch
        {
            Key.Down => Math.Min(index + 1, vm.FilteredMods.Count - 1),
            Key.Up => Math.Max(index - 1, 0),
            Key.Home => 0,
            Key.End => vm.FilteredMods.Count - 1,
            _ => -1
        };
        if (targetIndex >= 0)
        {
            var target = vm.FilteredMods[targetIndex];
            if (shift) vm.SelectRange(target, additive: control);
            else if (control) vm.FocusedMod = target;
            else vm.FocusMod(target);
            ModList.ScrollIntoView(target);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && vm.FocusedMod is { } focused)
        {
            vm.ToggleSelected(focused);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 点击背景关闭"加入配置文件"弹窗
    /// </summary>
    private void ModCard_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control card
            || card.DataContext is not ModItemViewModel mod
            || DataContext is not LibraryViewModel vm)
        {
            return;
        }

        ShowModContextMenu(card, mod, vm);
        e.Handled = true;
    }

    private void OnModListContextReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || e.Source is not Visual source ||
            DataContext is not LibraryViewModel vm || vm.ModLink?.IsOpen == true || vm.ModOnline?.IsOpen == true) return;
        var card = source.GetVisualAncestors().Prepend(source).OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("mod-card") && b.DataContext is ModItemViewModel);
        if (card?.DataContext is not ModItemViewModel mod) return;
        ShowModContextMenu(card, mod, vm);
        e.Handled = true;
    }

    private static void ShowModContextMenu(Control card, ModItemViewModel mod, LibraryViewModel vm)
    {
        if (vm.ModOnline?.IsOpen == true) return;
        var entries = new List<object>
        {
            new ContextMenuItem("查看详情", vm.ShowModDetailCommand, "Info") { CommandParameter = mod },
            new ContextMenuItem("模组链接…", vm.OpenModLinkCommand, "Globe") { CommandParameter = mod },
        };

        if (mod.HasPlatformLink)
            entries.Add(new ContextMenuItem("打开来源网页", mod.OpenSourcePageCommand, "Globe"));
        if (mod.HasAuthorPage)
            entries.Add(new ContextMenuItem("打开作者主页", mod.OpenAuthorPageCommand, "Globe"));
        if (mod.CanCheckUpdates && vm.ModOnline != null)
        {
            entries.Add(new ContextMenuItem("检查和更新模组…", vm.OpenModOnlineCommand, "Refresh") { CommandParameter = mod });
        }

        if (mod.IsInCurrentProfile)
        {
            entries.Add(new ContextMenuItem(
                mod.IsEnabled ? "在当前 Profile 禁用" : "在当前 Profile 启用",
                vm.ToggleModEnabledCommand, "Check") { CommandParameter = mod });
            entries.Add(new ContextMenuItem("从当前 Profile 移除", vm.RemoveModFromProfileCommand, "Close")
            {
                CommandParameter = mod,
            });
        }
        else
        {
            entries.Add(new ContextMenuItem("加入 Profile…", vm.OpenAddToProfileDialogCommand, "Add")
            {
                CommandParameter = mod,
            });
        }

        if (mod.OpenFolderCommand is { } openFolder)
        {
            entries.Add(AppContextMenu.Separator);
            entries.Add(new ContextMenuItem("打开文件位置", openFolder, "Folder"));
        }

        entries.Add(AppContextMenu.Separator);
        entries.Add(new ContextMenuItem("从 Library 删除", vm.DeleteModCommand, "Clean")
        {
            CommandParameter = mod,
            IsDanger = true,
        });

        AppContextMenu.Show(entries, card);
    }

    /// <summary>
    /// 点击背景关闭"加入配置文件"弹窗
    /// </summary>
    private void OnAddToProfileBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.CancelAddToProfileCommand.Execute(null);
        }
    }

    /// <summary>
    /// 点击背景关闭详情面板
    /// </summary>
    private void OnDetailBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.CloseDetailPanelCommand.Execute(null);
        }
    }

    /// <summary>点击模组卡片图标：在主窗口顶层的覆盖层预览原图（无图标时无操作）。</summary>
    private void PreviewImage_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (sender is Border { DataContext: ModItemViewModel { IconBitmap: { } icon } mod }
            && DataContext is LibraryViewModel vm)
        {
            vm.ShowImagePreview(icon, mod.Name);
        }
    }

    private void OnImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm) vm.OpenImportCommand.Execute(null);
    }

    private async void OnDownloadOnlineClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel { ModOnline: { CanDownload: true } online } ||
            TopLevel.GetTopLevel(this) is not { } owner) return;
        if (online.IsAyaka) { online.OpenBrowserDownload(); return; }
        var selected = online.SelectedFile;
        var suggestedName = online.SuggestedDownloadFileName;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiLocalization.Current.Translate("保存模组压缩包"), SuggestedFileName = suggestedName, DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(UiLocalization.Current.Translate("模组压缩包")) { Patterns = new[] { "*.zip", "*.7z", "*.rar", "*.tar", "*.gz", "*.bz2", "*.xz" } },
                FilePickerFileTypes.All
            }
        });
        if (online.IsOpen && ReferenceEquals(selected, online.SelectedFile) && file?.TryGetLocalPath() is { } path)
            await online.DownloadAsync(path);
    }

    private async void OnChooseOnlineBrowserFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel { ModOnline: { IsWaitingForBrowser: true } online } ||
            TopLevel.GetTopLevel(this) is not { } owner) return;
        var selected = online.SelectedFile;
        var completion = online.Completion;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["BrowserDownload.ChooseFile"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiLocalization.Current.Translate("模组压缩包"))
                { Patterns = ["*.zip", "*.7z", "*.rar", "*.tar", "*.gz", "*.bz2", "*.xz"] }]
        });
        if (online.IsOpen && online.IsWaitingForBrowser && ReferenceEquals(completion, online.Completion) && ReferenceEquals(selected, online.SelectedFile) &&
            files.FirstOrDefault()?.TryGetLocalPath() is { } path) await online.SelectBrowserFileAsync(path);
    }
}
