using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Controls;

namespace HD2Mimnt.UI.Services;

public sealed class PageJellyAnimator
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(0.9);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly LinearEasing Linear = new();
    private const double Amplitude = 0.14;
    private const double HorizontalRatio = 0.35;
    private const double MaxHeightChange = 12;
    private const double MaxWidthChange = 32;
    private const double StaggerCycles = 0.13;
    private const int KeyFrameSteps = 16;

    private readonly Control _root;
    private readonly Control? _excludedRoot;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<Control, TargetEntry> _entries = new();

    public PageJellyAnimator(Control root, Control? excludedRoot = null)
    {
        _root = root;
        _excludedRoot = excludedRoot;
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => AttachTargets();
    }

    public bool IsActive { get; private set; }

    public int ActiveTargetCount => _entries.Count;

    public void Toggle()
    {
        if (IsActive) Stop(); else Start();
    }

    public void Start()
    {
        if (IsActive) return;
        IsActive = true;
        AttachTargets();
        _refreshTimer.Start();
    }

    public void Stop()
    {
        IsActive = false;
        _refreshTimer.Stop();
        foreach (var entry in _entries.Values) entry.Restore();
        _entries.Clear();
    }

    private void AttachTargets()
    {
        if (!IsActive) return;

        foreach (var disposed in _entries.Values.Where(entry => entry.Visual.IsDisposed).ToArray())
            _entries.Remove(disposed.Control);

        var targets = _root.GetVisualDescendants().OfType<Control>()
            .Where(IsTarget)
            .Select(control => (Control: control, Position: control.TranslatePoint(default, _root) ?? default))
            .OrderBy(item => item.Position.Y)
            .ThenBy(item => item.Position.X)
            .ToList();

        for (var index = 0; index < targets.Count; index++)
        {
            var control = targets[index].Control;
            var visual = ElementComposition.GetElementVisual(control);
            if (visual == null || visual.IsDisposed) continue;

            if (_entries.TryGetValue(control, out var existing))
            {
                if (ReferenceEquals(existing.Visual, visual))
                {
                    existing.UpdateCenterPoint();
                    continue;
                }
                existing.Restore();
                _entries.Remove(control);
            }

            _entries.Add(control, TargetEntry.Attach(control, visual, index));
        }
    }

    private bool IsTarget(Control control)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        if (control.TemplatedParent != null) return false;
        if (_excludedRoot != null
            && (ReferenceEquals(control, _excludedRoot) || control.GetVisualAncestors().Contains(_excludedRoot)))
            return false;

        return control switch
        {
            TextBlock or Border or Button or Image or TextBox or ComboBox or CheckBox or ToggleSwitch
                or ProgressBar or Slider or FANavigationViewItem or UtilityIcon or FAInfoBadge => true,
            Shape => true,
            _ => false
        };
    }

    private sealed class TargetEntry
    {
        private readonly CompositionVisual _visual;
        private readonly Vector3D _originalScale;
        private readonly Vector3D _originalCenterPoint;
        private readonly Vector3DKeyFrameAnimation _animation;

        private TargetEntry(Control control, CompositionVisual visual, Vector3D originalScale,
            Vector3D originalCenterPoint, Vector3DKeyFrameAnimation animation)
        {
            Control = control;
            _visual = visual;
            _originalScale = originalScale;
            _originalCenterPoint = originalCenterPoint;
            _animation = animation;
        }

        public Control Control { get; }

        public CompositionVisual Visual => _visual;

        public static TargetEntry Attach(Control control, CompositionVisual visual, int rank)
        {
            var amount = Math.Min(Amplitude, Math.Min(
                MaxHeightChange / control.Bounds.Height,
                MaxWidthChange / (control.Bounds.Width * HorizontalRatio)));
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Duration = Period;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueAfterDelay;
            animation.DelayTime = TimeSpan.FromSeconds((rank * StaggerCycles % 1.0) * Period.TotalSeconds);
            for (var step = 0; step <= KeyFrameSteps; step++)
            {
                var progress = step / (double)KeyFrameSteps;
                var wave = Math.Sin(2 * Math.PI * progress);
                var scaleY = 1 + amount * wave;
                var scaleX = 1 - amount * HorizontalRatio * wave;
                animation.InsertKeyFrame((float)progress, new Vector3D(scaleX, scaleY, 1), Linear);
            }

            var originalScale = visual.Scale;
            var originalCenterPoint = visual.CenterPoint;
            visual.CenterPoint = new Vector3D(control.Bounds.Width / 2, control.Bounds.Height, 0);
            visual.StartAnimation("Scale", animation);
            return new TargetEntry(control, visual, originalScale, originalCenterPoint, animation);
        }

        public void UpdateCenterPoint()
        {
            if (_visual.IsDisposed) return;
            var center = new Vector3D(Control.Bounds.Width / 2, Control.Bounds.Height, 0);
            if (_visual.CenterPoint != center) _visual.CenterPoint = center;
        }

        public void Restore()
        {
            if (_visual.IsDisposed) return;
            _visual.StopAnimation("Scale");
            // 服务端会保留动画最后一帧的值；用一帧复位动画把属性写回原值。
            var reset = _visual.Compositor.CreateVector3DKeyFrameAnimation();
            reset.Duration = TimeSpan.FromMilliseconds(1);
            reset.IterationCount = 1;
            reset.InsertKeyFrame(0f, _originalScale);
            reset.InsertKeyFrame(1f, _originalScale);
            _visual.StartAnimation("Scale", reset);
            _visual.CenterPoint = _originalCenterPoint;
        }
    }
}
