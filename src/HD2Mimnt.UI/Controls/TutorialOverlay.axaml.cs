using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

/// <summary>只呈现引导：模糊页面镜像、清晰目标镜像和说明卡片。原控件不被移动或执行。</summary>
public partial class TutorialOverlay : UserControl
{
    public static readonly StyledProperty<Control?> SourceProperty =
        AvaloniaProperty.Register<TutorialOverlay, Control?>(nameof(Source));
    public Control? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private TutorialViewModel? _viewModel;
    private Control? _target;
    private bool _attached;
    private bool _bringTargetIntoView;

    public TutorialOverlay()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; AttachState(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; DetachState(); ClearPreview(); };
        DataContextChanged += (_, _) => { if (_attached) AttachState(); };
        LayoutUpdated += (_, _) => { if (_viewModel?.IsOpen == true) UpdateTarget(); };
        AddHandler(KeyDownEvent, OnTutorialKeyDown, RoutingStrategies.Tunnel);
    }

    private void AttachState()
    {
        DetachState();
        _viewModel = DataContext as TutorialViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnStepChanged;
        RefreshStep();
    }

    private void DetachState()
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnStepChanged;
        _viewModel = null;
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TutorialViewModel.CurrentStep) or nameof(TutorialViewModel.IsOpen)) RefreshStep();
    }

    private void RefreshStep()
    {
        ClearPreview();
        if (_viewModel?.IsOpen != true) return;
        _bringTargetIntoView = true;
        // 页面切换及分类展开完成布局后，再读取实际控件坐标。
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || _viewModel?.IsOpen != true) return;
            UpdateTarget();
            NextButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void ClearPreview()
    {
        _target = null;
        BlurredPage.Background = null;
        TargetPreview.Child = null;
        TargetFrame.IsVisible = false;
    }

    private void UpdateTarget()
    {
        if (Source == null || _viewModel == null) return;
        BlurredPage.Background ??= new VisualBrush(Source) { Stretch = Stretch.Fill };
        BlurredPage.Width = Source.Bounds.Width;
        BlurredPage.Height = Source.Bounds.Height;
        var sourceOrigin = Source.TranslatePoint(default, this) ?? default;
        BlurredPage.Margin = new Thickness(sourceOrigin.X, sourceOrigin.Y, 0, 0);
        var target = Source.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == _viewModel.CurrentStep.TargetKey && c.IsEffectivelyVisible);
        var point = target?.TranslatePoint(default, this);
        var available = target != null && point != null && target.Bounds.Width > 0 && target.Bounds.Height > 0;
        TargetFrame.IsVisible = available;
        TargetUnavailable.IsVisible = !available;
        if (!available)
        {
            PlaceCard(new Rect(Bounds.Width / 2, Bounds.Height / 2, 0, 0));
            return;
        }
        if (_bringTargetIntoView)
        {
            _bringTargetIntoView = false;
            target!.BringIntoView();
        }
        if (_target != target)
        {
            _target = target;
            TargetPreview.Child = new Border { Background = new VisualBrush(target!) { Stretch = Stretch.Fill } };
        }
        var rect = new Rect(point!.Value, target!.Bounds.Size);
        var frame = rect.Inflate(6);
        Canvas.SetLeft(TargetFrame, frame.X);
        Canvas.SetTop(TargetFrame, frame.Y);
        TargetFrame.Width = frame.Width;
        TargetFrame.Height = frame.Height;
        PlaceCard(frame);
    }

    private void PlaceCard(Rect target)
    {
        const double gap = 16;
        ExplanationCard.Width = Math.Min(350, Math.Max(280, Bounds.Width - 24));
        var width = ExplanationCard.Width;
        var height = ExplanationCard.Bounds.Height;
        double left, top;
        if (target.Right + gap + width <= Bounds.Width - 12)
        { left = target.Right + gap; top = target.Top; }
        else if (target.Left - gap - width >= 12)
        { left = target.Left - gap - width; top = target.Top; }
        else if (target.Bottom + gap + height <= Bounds.Height - 12)
        { left = target.Left; top = target.Bottom + gap; }
        else
        { left = target.Left; top = target.Top - gap - height; }
        Canvas.SetLeft(ExplanationCard, Math.Clamp(left, 12, Math.Max(12, Bounds.Width - width - 12)));
        Canvas.SetTop(ExplanationCard, Math.Clamp(top, 12, Math.Max(12, Bounds.Height - height - 12)));
    }

    private void OnTutorialKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _viewModel?.CloseCommand.Execute(null);
        e.Handled = true;
    }
}
