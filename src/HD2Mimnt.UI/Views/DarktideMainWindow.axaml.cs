using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.ViewModels.Darktide;

namespace HD2Mimnt.UI.Views;

public partial class DarktideMainWindow : Window
{
    private DarktideMainWindowViewModel? _viewModel;

    public DarktideMainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        NavigationView.SelectedItem = NavigationView.MenuItems[0];
    }

    private void AttachViewModel()
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as DarktideMainWindowViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateNavigationSelection();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DarktideMainWindowViewModel.SelectedTabIndex)) UpdateNavigationSelection();
    }

    private void UpdateNavigationSelection()
    {
        var index = _viewModel?.SelectedTabIndex ?? 0;
        NavigationView.SelectedItem = NavigationView.MenuItems.OfType<FANavigationViewItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == index.ToString());
    }

    private void OnNavigationSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;
        if (e.SelectedItem is not FANavigationViewItem item || !int.TryParse(item.Tag?.ToString(), out var index)) return;
        _viewModel.SelectedTabIndex = index;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.Library.Cancel();
        _viewModel.LoadOrder.Cancel();
        _viewModel.Profiles.Cancel();
        _viewModel.Loader.Cancel();
        _viewModel.Settings.Cancel();
    }
}
