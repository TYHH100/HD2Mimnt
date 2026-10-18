using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModCompatibilityOverlay : UserControl
{
    private ModCompatibilityViewModel? _model;
    public ModCompatibilityOverlay()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { UiLocalization.Current.PropertyChanged += LanguageChanged; ObserveModel(); };
        DetachedFromVisualTree += (_, _) => { UiLocalization.Current.PropertyChanged -= LanguageChanged; Unobserve(); };
        DataContextChanged += (_, _) => { if (VisualRoot != null) ObserveModel(); };
    }
    private void ObserveModel()
    {
        Unobserve();
        _model = DataContext as ModCompatibilityViewModel;
        if (_model != null) { _model.PropertyChanged += ModelChanged; _model.Relocalize(); }
    }
    private void Unobserve() { if (_model != null) _model.PropertyChanged -= ModelChanged; _model = null; }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModCompatibilityViewModel.IsOpen) && _model?.IsOpen == true)
            Dispatcher.UIThread.Post(() => { if (_model?.IsOpen == true) CloseCompatibilityButton.Focus(); });
    }
    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(UiLocalization.Revision)) _model?.Relocalize(); }
    private async void CopyReport(object? sender, RoutedEventArgs e)
    {
        if (_model == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            var text = _model.Summary + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine,
                _model.Mods.Select(m => m.Summary + Environment.NewLine + string.Join(Environment.NewLine,
                    m.Details.Concat(m.PlanDetails).Select(d => d.Title + Environment.NewLine + d.Text)) + Environment.NewLine + m.OperationSummary));
            await clipboard.SetTextAsync(text);
            _model.Message = UiLocalization.Current["Compatibility.Copied"];
        }
        catch (Exception ex) { if (_model != null) _model.Message = ex.Message; }
    }
}
