using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HD2Mimnt.UI.Views;

public partial class PluginManagementView : UserControl
{
    public PluginManagementView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
