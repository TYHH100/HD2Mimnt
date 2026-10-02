using Avalonia.Controls;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 从依赖注入容器获取 ViewModel
        if (Program.AppHost != null)
        {
            DataContext = Program.AppHost.Services.GetRequiredService<MainWindowViewModel>();
        }
    }
}