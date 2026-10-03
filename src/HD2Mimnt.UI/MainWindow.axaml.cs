using Avalonia.Controls;

namespace HD2Mimnt.UI;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
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
