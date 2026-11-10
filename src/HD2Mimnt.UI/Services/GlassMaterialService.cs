using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 浅色主题的窗口玻璃材质。两种实现：
/// <list type="number">
/// <item>系统丙烯酸（默认）：实时反映窗口背后，窗口能正常出现在截图与录屏里；模糊程度由系统固定。</item>
/// <item>自绘实时玻璃（可选）：<see cref="LiveBackdropService"/> 取窗口背后的真实画面，模糊半径由「模糊强度」调节，
/// 代价是必须把窗口排除在截屏之外——截图/录屏里窗口是一片空白。仅在用户开启「可调模糊玻璃」时启用。</item>
/// </list>
/// 着色层与各表面的透明度由应用提供：「毛玻璃强度」控制着色不透明度，0 为关闭（保持原来的纯色观感），
/// 100 为最通透。平台完全没有模糊能力（无头测试、非 Windows）时退回不透明外观，避免露出未模糊的桌面。
/// </summary>
public static class GlassMaterialService
{
    public const double DefaultIntensity = 60;
    public const double MinIntensity = 0;
    public const double MaxIntensity = 100;

    /// <summary>最强强度下窗口着色层的不透明度：再低会让深色壁纸前的正文难以辨认。</summary>
    private const double FrostedWindowOpacity = 0.62;

    /// <summary>最强强度下卡片的不透明度；卡片始终比背景更实，保证文字对比度。</summary>
    private const double FrostedCardOpacity = 0.90;

    /// <summary>卡片不透明度的下限与设置页滑块一致。</summary>
    private const double MinCardPercent = 5;

    /// <summary>当前实际生效的变体是否为浅色（“跟随系统”由平台解析）。设置页用它决定是否展示毛玻璃滑块。</summary>
    public static bool IsLightThemeActive() =>
        (global::Avalonia.Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark).Equals(ThemeVariant.Light);

    /// <summary>是否有可用的实时玻璃实现（取屏）。</summary>
    public static bool IsLiveBackdropSupported => OperatingSystem.IsWindows();

    private static readonly WindowTransparencyLevel[] BlurHints =
    {
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur
    };

    // Windows 11 22H2+ 的系统背衬：让原生标题栏与内容共用同一层丙烯酸（仅兜底路径使用）。
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtAuto = 0;
    private const int DwmsbtTransientWindow = 3;
    private const int FirstSystemBackdropBuild = 22621;

    private static readonly ConditionalWeakTable<Window, MaterialState> States = new();

    // 写 TransparencyLevelHint 会同步触发 ActualTransparencyLevel 变更事件，用重入保护避免自我递归。
    private static bool _applying;

    private static LiveBackdropService? _liveSource;

    /// <summary>实时玻璃的最新帧；界面收到 <see cref="LiveFrameUpdated"/> 后重绘。</summary>
    public static IImage? LiveFrame => _liveSource?.Frame;

    /// <summary>实时玻璃取屏时四周多出的一圈（DIP）：图层要按它向外偏移，模糊边缘才不会取到空像素。</summary>
    public static double LiveHeadroomDips => _liveSource?.HeadroomDips ?? LiveBackdropService.BlurMargin;

    /// <summary>实时玻璃有新帧（UI 线程）。</summary>
    public static event EventHandler? LiveFrameUpdated;

    /// <summary>强度换算出的窗口着色层不透明度（1 为不透明）。</summary>
    public static double WindowOpacity(double intensity) => Interpolate(1, FrostedWindowOpacity, intensity);

    /// <summary>强度换算出的卡片不透明度：只在毛玻璃更通透时下调，不抬高用户已设置的更低值。</summary>
    public static double CardOpacity(double intensity, double cardOpacityPercent) =>
        Math.Min(Math.Clamp(cardOpacityPercent, MinCardPercent, 100) / 100d,
            Interpolate(1, FrostedCardOpacity, intensity));

    /// <summary>是否需要毛玻璃：仅浅色主题，且没有自定义背景图（背景图本身就是底层内容）。</summary>
    public static bool IsRequested(ThemeVariant? variant, double intensity, bool hasBackgroundImage) =>
        variant == ThemeVariant.Light && !hasBackgroundImage && intensity > MinIntensity;

    /// <summary>平台是否提供了系统背景模糊（兜底路径的判据）。</summary>
    public static bool IsBlurred(WindowTransparencyLevel level) =>
        level.Equals(WindowTransparencyLevel.AcrylicBlur) || level.Equals(WindowTransparencyLevel.Blur);

    /// <summary>
    /// 计算并发布窗口与各表面的透明度，返回是否真的启用了玻璃。
    /// 未启用时按原有规则发布（背景图半透明卡片、纯色背景全不透明）。
    /// </summary>
    /// <param name="screenshotFriendly">
    /// 默认 true：走系统丙烯酸，窗口能出现在截图/录屏里。用户显式关闭时才启用自绘取屏，换取可调的模糊半径。
    /// </param>
    public static bool Apply(Window? window, ThemeVariant? variant, double intensity, double blurIntensity,
        bool hasBackgroundImage, double cardOpacityPercent, bool screenshotFriendly = true)
    {
        if (_applying) return false;
        _applying = true;
        try
        {
            var requested = IsRequested(variant, intensity, hasBackgroundImage);
            MaterialState? state = null;
            var live = false;

            if (window is not null)
            {
                state = States.GetValue(window, static owner => new MaterialState(owner));
                state.Remember(variant, intensity, blurIntensity, hasBackgroundImage, cardOpacityPercent, screenshotFriendly);

                // 实时取屏必须把窗口排除在截屏之外，代价是窗口在截图/录屏里是空白的；
                // 默认（screenshotFriendly）走系统玻璃：窗口能被截图，宁可模糊半径不可调。
                if (requested && !screenshotFriendly)
                {
                    state.Live ??= new LiveBackdropService(window);
                    live = state.Live.TryStart();
                }

                if (!live) state.Live?.Stop();

                try { state.RequestBlur(requested && !live); }
                catch (Exception) { requested = false; live = false; }
            }

            var level = window?.ActualTransparencyLevel ?? WindowTransparencyLevel.None;
            var active = live || (requested && state is not null && IsBlurred(level));
            Publish(active, live, intensity, hasBackgroundImage, cardOpacityPercent, window, state);
            return active;
        }
        finally
        {
            _applying = false;
        }
    }

    private static void Publish(bool active, bool live, double intensity, bool hasBackgroundImage,
        double cardOpacityPercent, Window? window, MaterialState? state)
    {
        if (window is not null && state is not null)
        {
            // 实时玻璃自己画满整窗，窗口本身保持不透明；兜底路径才需要窗口透明以露出系统背衬。
            state.ApplyWindowMaterial(window, active && !live);
            SetSystemBackdrop(window, ref state.BackdropApplied, active && !live);
            AttachLiveSource(live ? state.Live : null);
        }
        else
        {
            AttachLiveSource(null);
        }

        if (global::Avalonia.Application.Current is not { } app) return;

        var percent = Math.Clamp(cardOpacityPercent, MinCardPercent, 100) / 100d;
        if (active)
        {
            // 着色只由主窗口根容器的那一层承担：内容区与顶栏/底栏不再叠加同类底色，
            // 整个窗口才是同一块玻璃，而不是几块深浅不一的半透明区域。
            app.Resources["ShellSurfaceOpacity"] = WindowOpacity(intensity);
            app.Resources["ChromeSurfaceOpacity"] = 0d;
            app.Resources["ContentSurfaceOpacity"] = 0d;
            app.Resources["CardSurfaceOpacity"] = CardOpacity(intensity, cardOpacityPercent);
            return;
        }

        // 未启用：根容器不着色，交回 Window 样式的窗口底色（皮肤主题也依赖这一层）。
        app.Resources["ShellSurfaceOpacity"] = 0d;
        app.Resources["CardSurfaceOpacity"] = percent;
        app.Resources["ChromeSurfaceOpacity"] = hasBackgroundImage ? percent : 1d;
        app.Resources["ContentSurfaceOpacity"] = hasBackgroundImage ? 0d : 1d;
    }

    private static void AttachLiveSource(LiveBackdropService? source)
    {
        if (ReferenceEquals(_liveSource, source)) return;

        if (_liveSource is not null) _liveSource.FrameUpdated -= OnLiveFrameUpdated;
        _liveSource = source;
        if (_liveSource is not null) _liveSource.FrameUpdated += OnLiveFrameUpdated;
    }

    private static void OnLiveFrameUpdated(object? sender, EventArgs e) =>
        LiveFrameUpdated?.Invoke(null, EventArgs.Empty);

    /// <summary>Windows 11 22H2+ 才支持系统背衬类型；旧系统和受限环境下保留 Avalonia 自身提供的丙烯酸。</summary>
    private static void SetSystemBackdrop(Window window, ref bool? applied, bool enabled)
    {
        if (applied == enabled) return;
        applied = enabled;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstSystemBackdropBuild)) return;
        if (window.TryGetPlatformHandle() is not { } handle) return;

        var type = enabled ? DwmsbtTransientWindow : DwmsbtAuto;
        try
        {
            _ = DwmSetWindowAttribute(handle.Handle, DwmwaSystemBackdropType, ref type, sizeof(int));
        }
        catch (Exception)
        {
            // 系统不支持该属性时保留现有观感，不阻断界面。
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static double Interpolate(double opaque, double frosted, double intensity)
    {
        var strength = Math.Clamp(intensity, MinIntensity, MaxIntensity) / MaxIntensity;
        return Math.Round(opaque + (frosted - opaque) * strength, 3);
    }

    /// <summary>记录每个窗口最近一次的外观参数，用于平台在显示后才确定透明度级别时重算。</summary>
    private sealed class MaterialState
    {
        private readonly Window _window;
        private ThemeVariant? _variant;
        private double _intensity = DefaultIntensity;
        private double _blurIntensity = LiveBackdropService.DefaultBlur;
        private bool _hasBackgroundImage;
        private double _cardOpacityPercent = 100;
        private bool _screenshotFriendly;
        private bool _blurRequested;
        private bool _closed;

        public MaterialState(Window window)
        {
            _window = window;
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.Closed += OnWindowClosed;
        }

        /// <summary>系统背衬类型的当前状态；null 表示还没设置过。</summary>
        public bool? BackdropApplied;

        /// <summary>实时取屏的实现；仅在需要玻璃时创建。</summary>
        public LiveBackdropService? Live { get; set; }

        public void Remember(ThemeVariant? variant, double intensity, double blurIntensity,
            bool hasBackgroundImage, double cardOpacityPercent, bool screenshotFriendly)
        {
            _variant = variant;
            _intensity = intensity;
            _blurIntensity = blurIntensity;
            _hasBackgroundImage = hasBackgroundImage;
            _cardOpacityPercent = cardOpacityPercent;
            _screenshotFriendly = screenshotFriendly;
        }

        public void RequestBlur(bool requested)
        {
            if (_blurRequested == requested) return;

            _blurRequested = requested;
            try
            {
                _window.TransparencyLevelHint = requested ? BlurHints : Array.Empty<WindowTransparencyLevel>();
            }
            catch (Exception)
            {
                // 平台不支持透明度：撤销请求并保持不透明。
                _blurRequested = false;
                throw;
            }
        }

        /// <summary>
        /// 兜底路径的丙烯酸需要窗口自身透明才能露出系统背衬；关闭时清除本地值，回到 Window 样式的主题底色，
        /// 否则会盖掉 Ore/Helldivers 等皮肤对 Surface0Brush 的覆盖。实时玻璃自己画满整窗，不使用这条路径。
        /// </summary>
        public void ApplyWindowMaterial(Window window, bool active)
        {
            if (active)
            {
                if (!ReferenceEquals(window.Background, Brushes.Transparent)) window.Background = Brushes.Transparent;
            }
            else if (window.IsSet(TemplatedControl.BackgroundProperty))
            {
                window.ClearValue(TemplatedControl.BackgroundProperty);
            }
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != TopLevel.ActualTransparencyLevelProperty || _closed) return;
            Apply(_window, _variant, _intensity, _blurIntensity, _hasBackgroundImage, _cardOpacityPercent, _screenshotFriendly);
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (_closed) return;
            _closed = true;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.Closed -= OnWindowClosed;
            Live?.Dispose();
            Live = null;
        }
    }
}
