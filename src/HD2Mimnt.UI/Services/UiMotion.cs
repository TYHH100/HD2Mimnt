namespace HD2Mimnt.UI.Services;

/// <summary>界面动效总开关。设置页保存或应用启动时同步，窗口用 <c>motion</c> 样式类承载。</summary>
public static class UiMotion
{
    /// <summary>悬停、底色等即时反馈。</summary>
    public static readonly TimeSpan FastDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>切页与浮层入场等整体过渡。</summary>
    public static readonly TimeSpan StandardDuration = TimeSpan.FromMilliseconds(160);

    public static event EventHandler? Changed;

    public static bool IsEnabled { get; private set; } = true;

    public static void Apply(bool enabled)
    {
        if (IsEnabled == enabled) return;
        IsEnabled = enabled;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
