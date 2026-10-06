using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        var isTextInput = e.Source is TextBox;
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
            vm.SelectAllCommand.Execute(null);
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
    /// 普通单击只聚焦（ListBox 单选模式处理）并清空多选，不会弹出批量操作条。
    /// </summary>
    private void ModCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card
            || card.DataContext is not ModItemViewModel mod
            || DataContext is not LibraryViewModel vm)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            vm.ShowModDetailCommand.Execute(mod);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ToggleSelected(mod);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.SelectRange(mod);
            e.Handled = true;
        }
        else
        {
            vm.ClearSelectionCommand.Execute(null);
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

        var entries = new List<object>
        {
            new ContextMenuItem("查看详情", vm.ShowModDetailCommand, "Info") { CommandParameter = mod },
        };

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
        e.Handled = true;
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

    /// <summary>
    /// 全页拖拽导入：悬停显示提示层，松开批量导入压缩包（旧版全窗口拖拽导入的等价实现）
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            DragHintBorder.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DragHintBorder.IsVisible = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DragHintBorder.IsVisible = false;
        if (DataContext is not LibraryViewModel vm) return;

        var paths = e.DataTransfer.TryGetFiles()
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths is not { Count: > 0 }) return;

        await vm.ImportPathsAsync(paths);
    }

    private async void OnImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择模组文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("模组压缩包") { Patterns = new[] { "*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.xz" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        var paths = files.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>();
        await vm.ImportPathsAsync(paths);
    }
}
