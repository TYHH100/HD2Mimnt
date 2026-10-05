using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        vm.SyncListSelection(
            e.AddedItems.OfType<ModItemViewModel>(),
            e.RemovedItems.OfType<ModItemViewModel>());
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
                new FilePickerFileType("模组压缩包") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        var paths = files.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>();
        await vm.ImportPathsAsync(paths);
    }
}
