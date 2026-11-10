using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using System.Collections.Specialized;
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

    /// <summary>内置页面数量；插件页面从该索引开始追加到 TabControl。</summary>
    private const int FixedPageCount = MainWindowViewModel.PluginPageOffset;

    /// <summary>导航项 Tag 起点，避免与内置页面的 0-6 冲突。</summary>
    private const int PluginNavTagBase = 100;

    private readonly PageJellyAnimator _pageJelly;
    private bool _closeConfirmed;
    private bool _closingInFlight;
    private MainWindowViewModel? _viewModel;
    private bool _importingDrop;
    private int _versionClickCount;
    private DateTime _versionClickWindowStartUtc;
    private bool _pluginSyncScheduled;

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
        // 实时玻璃每帧换一张位图，直接喂给图层，避免为每帧改动绑定。
        GlassMaterialService.LiveFrameUpdated += OnGlassFrameUpdated;
        Closed += (_, _) =>
        {
            GlassMaterialService.LiveFrameUpdated -= OnGlassFrameUpdated;
            GlassBackdropImage.Source = null;
            ImportDragHint.IsVisible = false;
            _pageJelly.Stop();
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.PluginPages.CollectionChanged -= OnPluginContributionsChanged;
                _viewModel.PluginMenus.CollectionChanged -= OnPluginContributionsChanged;
            }
        };
        NavigationView.SelectedItem = NavigationView.MenuItems[0];
    }

    /// <summary>
    /// 标题栏拖动与双击最大化：扩展客户区后没有系统标题栏可拖，空白处就是这块命中区。
    /// 右上角的最小化/最大化/关闭由主题自绘在更上层，不会被这里截走。
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized) return;
        BeginMoveDrag(e);
    }

    /// <summary>
    /// 把最新一帧铺到窗口下方：取屏范围比窗口四周各多一圈（<see cref="LiveBackdropService.BlurMargin"/>），
    /// 因此图层要放大同样的尺寸并向外偏移，模糊边缘才不会在窗口边界取到空像素。
    /// 尺寸与偏移必须无条件写入：Image 的 Width 初始是 NaN，任何「值变了才更新」的判断
    /// （<c>NaN &gt; 0.5</c> 恒为 false）都会吞掉首次赋值，整块背景就偏移一整圈。
    /// </summary>
    private void OnGlassFrameUpdated(object? sender, EventArgs e)
    {
        if (GlassMaterialService.LiveFrame is not { } frame) return;

        GlassBackdropImage.Source = frame;

        // frame.Size 已经是 DIP（位图按窗口缩放比例建 DPI），与图层尺寸一一对应。
        var headroom = GlassMaterialService.LiveHeadroomDips;
        GlassBackdropImage.Width = frame.Size.Width;
        GlassBackdropImage.Height = frame.Size.Height;
        Canvas.SetLeft(GlassBackdropImage, -headroom);
        Canvas.SetTop(GlassBackdropImage, -headroom);
        GlassBackdropImage.InvalidateVisual();
    }

    private void AttachNavigation()
    {
        ImportDragHint.IsVisible = false;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PluginPages.CollectionChanged -= OnPluginContributionsChanged;
            _viewModel.PluginMenus.CollectionChanged -= OnPluginContributionsChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.PluginPages.CollectionChanged += OnPluginContributionsChanged;
            _viewModel.PluginMenus.CollectionChanged += OnPluginContributionsChanged;
        }

        SyncPluginNavigation();
        SyncPluginMenu();
        UpdateNavigationSelection();

        if (_viewModel != null)
        {
            SafeAsync.FireAndForget(_viewModel.RefreshPluginUiAsync(), "加载插件界面贡献");
        }
    }

    /// <summary>
    /// 插件页面与菜单可能在任意时刻刷新（安装 / 启停 / 授权），
    /// 合并同一帧内的多次集合变更，避免反复重建导航项。
    /// </summary>
    private void OnPluginContributionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_pluginSyncScheduled) return;
        _pluginSyncScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _pluginSyncScheduled = false;
            SyncPluginNavigation();
            SyncPluginMenu();
            UpdateNavigationSelection();
        });
    }

    /// <summary>按当前插件页面重建导航项与页面容器。</summary>
    private void SyncPluginNavigation()
    {
        for (var i = NavigationView.MenuItems.Count - 1; i >= 0; i--)
        {
            if (NavigationView.MenuItems[i] is FANavigationViewItem item
                && int.TryParse(item.Tag?.ToString(), out var tag)
                && tag >= PluginNavTagBase)
            {
                NavigationView.MenuItems.RemoveAt(i);
            }
        }

        while (PageHost.Items.Count > FixedPageCount)
        {
            PageHost.Items.RemoveAt(PageHost.Items.Count - 1);
        }

        if (_viewModel is null) return;

        for (var i = 0; i < _viewModel.PluginPages.Count; i++)
        {
            var page = _viewModel.PluginPages[i];
            var navItem = new FANavigationViewItem
            {
                Content = page.Title,
                Tag = PluginNavTagBase + i,
                IconSource = new FASymbolIconSource { Symbol = new UtilityIcon { Kind = page.Icon ?? "Plugin" }.Symbol }
            };

            NavigationView.MenuItems.Add(navItem);
            PageHost.Items.Add(new TabItem
            {
                IsVisible = false,
                Content = new PluginPageView { DataContext = page }
            });
        }
    }

    /// <summary>把插件贡献的功能菜单项挂到导航栏底部按钮上。</summary>
    private void SyncPluginMenu()
    {
        var entries = _viewModel?.PluginMenus;
        PluginMenuButton.IsVisible = entries is { Count: > 0 };

        var flyout = new FAMenuFlyout();
        if (entries != null)
        {
            foreach (var entry in entries)
            {
                var item = new FAMenuFlyoutItem
                {
                    Text = entry.IsPermissionGranted ? entry.Label : $"{entry.Label}（需要授权）",
                    Tag = entry,
                    IsEnabled = entry.IsPermissionGranted
                };

                if (!string.IsNullOrWhiteSpace(entry.Icon))
                {
                    item.IconSource = new FASymbolIconSource { Symbol = new UtilityIcon { Kind = entry.Icon }.Symbol };
                }

                item.Click += OnPluginMenuEntryClick;
                flyout.Items.Add(item);
            }
        }

        PluginMenuButton.Flyout = flyout;
    }

    private async void OnPluginMenuEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not FAMenuFlyoutItem { Tag: PluginMenuEntry entry } || _viewModel is null) return;

        try
        {
            if (_viewModel.CreatePluginDialog(entry) is { } dialogSurface)
            {
                await PluginUiDialog.ShowAsync(this, dialogSurface);
                return;
            }

            if (await _viewModel.RunPluginMenuAsync(entry) is { } result)
            {
                await PluginCommandResultDialog.ShowAsync(this, result);
            }
        }
        catch (Exception ex)
        {
            CrashGuard.Report(ex, $"执行插件功能 {entry.Label}");
        }
    }

    private static List<string> GetDroppedPaths(DragEventArgs e) => e.DataTransfer.TryGetFiles()
        ?.Select(file => file.TryGetLocalPath())
        .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel?.ModelPreview?.IsOpen == true || _viewModel?.Compatibility?.IsOpen == true || _viewModel?.Tutorial.IsOpen == true)
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
        if (_viewModel == null || _viewModel.Tutorial.IsOpen || _viewModel.ModelPreview?.IsOpen == true || _viewModel.Compatibility?.IsOpen == true || _importingDrop || !_viewModel.LibraryViewModel.ImportCompletion.IsCompleted)
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
        var targetTag = index >= FixedPageCount
            ? PluginNavTagBase + (index - FixedPageCount)
            : index;

        NavigationView.SelectedItem = NavigationView.MenuItems.OfType<FANavigationViewItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == targetTag.ToString());
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

        if (index >= PluginNavTagBase)
        {
            _viewModel.NavigateToPluginPage(index - PluginNavTagBase);
            return;
        }

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
