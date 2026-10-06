using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HD2Mimnt.UI;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = App.TryGetAppIcon();
    }
}
