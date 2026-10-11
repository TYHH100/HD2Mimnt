using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HD2Mimnt.UI;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private MainWindowViewModel? _viewModel;
    private bool _importingDrop;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        // DragDrop 使用冒泡路由；即使子控件标记已处理，窗口仍统一接收外部文件。
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnTutorialShortcut, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachNavigation();
        Closed += (_, _) =>
        {
            ImportDragHint.IsVisible = false;
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
        NavigationView.SelectedItem = NavigationView.MenuItems[0];
    }

    private void AttachNavigation()
    {
        ImportDragHint.IsVisible = false;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateNavigationSelection();
    }

    private static List<string> GetDroppedPaths(DragEventArgs e) => e.DataTransfer.TryGetFiles()
        ?.Select(file => file.TryGetLocalPath())
        .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel?.Tutorial.IsOpen == true)
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            ImportDragHint.IsVisible = false;
            return;
        }
        if (GetDroppedPaths(e).Count == 0)
        {
            ImportDragHint.IsVisible = false;
            return; // 配置文件/模组内部排序继续交给原有处理器。
        }
        e.Handled = true;
        var canImport = _viewModel != null && !_importingDrop && _viewModel.LibraryViewModel.ImportCompletion.IsCompleted;
        e.DragEffects = canImport ? DragDropEffects.Copy : DragDropEffects.None;
        ImportDragHint.IsVisible = canImport;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        // 子控件之间移动也会产生 DragLeave；只在真正离开窗口时收起。
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            ImportDragHint.IsVisible = false;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        ImportDragHint.IsVisible = false;
        var paths = GetDroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        if (_viewModel == null || _viewModel.Tutorial.IsOpen || _importingDrop || !_viewModel.LibraryViewModel.ImportCompletion.IsCompleted)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        _importingDrop = true;
        try { await _viewModel.LibraryViewModel.ImportPathsAsync(paths); }
        finally { _importingDrop = false; }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTabIndex)) UpdateNavigationSelection();
    }

    private void OnTutorialShortcut(object? sender, KeyEventArgs e)
    {
        if (_viewModel?.Tutorial.IsOpen != true) return;
        if (e.Key == Key.Escape)
        {
            _viewModel.Tutorial.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is >= Key.D1 and <= Key.D5)
            e.Handled = true;
    }

    private void UpdateNavigationSelection()
    {
        var index = _viewModel?.SelectedTabIndex ?? 0;
        if (index >= 0 && index < NavigationView.MenuItems.Count)
            NavigationView.SelectedItem = NavigationView.MenuItems[index];
    }

    private void OnNavigationSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;
        var index = NavigationView.MenuItems.IndexOf(e.SelectedItem);
        if (index == _viewModel.SelectedTabIndex) return;
        switch (index)
        {
            case 0: _viewModel.NavigateToProfilesCommand.Execute(null); break;
            case 1: _viewModel.NavigateToLibraryCommand.Execute(null); break;
            case 2: _viewModel.NavigateToConflictsCommand.Execute(null); break;
            case 3: _viewModel.NavigateToToolsCommand.Execute(null); break;
            case 4: _viewModel.NavigateToSettingsCommand.Execute(null); break;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not ViewModels.MainWindowViewModel viewModel) return;

        e.Cancel = true;
        if (await viewModel.CanCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
