using Avalonia.Controls;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Views;

public partial class StorageStartupWindow : Window
{
    public event EventHandler? CancelRequested;
    public StorageStartupWindow()
    {
        InitializeComponent();
        Icon = App.TryGetAppIcon();
        CancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowFailure(string message)
    {
        StatusText.Text = message;
        Progress.IsVisible = false;
        CancelButton.Content = UiLocalization.Current["Common.Close"];
    }
}
