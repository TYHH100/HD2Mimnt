using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HD2Mimnt.UI;

public partial class MainWindow : Window
{
    private const int JellyClickThreshold = 7;
    private static readonly TimeSpan JellyClickWindow = TimeSpan.FromSeconds(3);

    private readonly PageJellyAnimator _pageJelly;
    private bool _closeConfirmed;
    private bool _closingInFlight;
    private MainWindowViewModel? _viewModel;
    private bool _importingDrop;
    private int _versionClickCount;
    private DateTime _versionClickWindowStartUtc;

    public MainWindow()
    {
        InitializeComponent();
        _pageJelly = new PageJellyAnimator(ApplicationSurface, StatusBar);
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
            _pageJelly.Stop();
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
        if (_viewModel?.Compatibility?.IsOpen == true || _viewModel?.Tutorial.IsOpen == true)
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

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        ImportDragHint.IsVisible = false;
        var paths = GetDroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        if (_viewModel == null || _viewModel.Tutorial.IsOpen || _viewModel.Compatibility?.IsOpen == true || _importingDrop || !_viewModel.LibraryViewModel.ImportCompletion.IsCompleted)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        _importingDrop = true;
        SafeAsync.Run(async () =>
        {
            try { await _viewModel!.LibraryViewModel.ImportPathsAsync(paths); }
            finally { _importingDrop = false; }
        }, "导入拖放文件");
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
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is >= Key.D1 and <= Key.D7)
            e.Handled = true;
    }

    private void UpdateNavigationSelection()
    {
        var index = _viewModel?.SelectedTabIndex ?? 0;
        NavigationView.SelectedItem = NavigationView.MenuItems.OfType<FANavigationViewItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == index.ToString());
    }

    private void OnVersionBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(VersionBadge).Properties.IsLeftButtonPressed) return;

        var now = DateTime.UtcNow;
        if (_versionClickCount == 0 || now - _versionClickWindowStartUtc > JellyClickWindow)
        {
            _versionClickCount = 0;
            _versionClickWindowStartUtc = now;
        }
        _versionClickCount++;
        if (_versionClickCount < JellyClickThreshold) return;

        _versionClickCount = 0;
        _pageJelly.Toggle();
        e.Handled = true;
    }

    private void OnNavigationSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;
        if (e.SelectedItem is not FANavigationViewItem item || !int.TryParse(item.Tag?.ToString(), out var index)) return;
        if (index == _viewModel.SelectedTabIndex) return;
        switch (index)
        {
            case 0: _viewModel.NavigateToProfilesCommand.Execute(null); break;
            case 1: _viewModel.NavigateToLibraryCommand.Execute(null); break;
            case 2: _viewModel.NavigateToConflictsCommand.Execute(null); break;
            case 3: _viewModel.NavigateToToolsCommand.Execute(null); break;
            case 4: _viewModel.NavigateToSettingsCommand.Execute(null); break;
            case 5: _viewModel.NavigateToBrowseCommand.Execute(null); break;
            case 6: _viewModel.NavigateToModCreationCommand.Execute(null); break;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _closingInFlight || DataContext is not ViewModels.MainWindowViewModel viewModel) return;

        e.Cancel = true;
        _closingInFlight = true;
        try
        {
            if (await viewModel.CanCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            // 关闭确认链抛异常时窗口不得卡死：记录后放行关闭，
            // 后续清理仍会由 Program.Main 的退出序列兜底。
            CrashGuard.Report(ex, "关闭确认失败");
            _closeConfirmed = true;
            Close();
        }
        finally { _closingInFlight = false; }
    }
}
