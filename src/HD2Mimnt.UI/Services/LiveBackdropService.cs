using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 实时玻璃背衬：按窗口位置截取屏幕内容，交给界面自己做模糊。
/// 这样“玻璃”背后是窗口背后的真实画面（实时变化），而模糊半径由应用决定——
/// Windows 的丙烯酸/Mica 半径由 DWM 固定、无法调节，这是唯一可自由调节模糊的路径。
/// 取屏前用 <c>WDA_EXCLUDEFROMCAPTURE</c> 把自身窗口排除，否则会把自己拍进画面形成无限递归。
/// </summary>
public sealed class LiveBackdropService : IDisposable
{
    public const double MinBlur = 0;
    public const double MaxBlur = 48;
    public const double DefaultBlur = 16;

    /// <summary>取屏时四周多取的一圈（DIP）：模糊半径再大，窗口边缘也不会取到空像素。</summary>
    public const double BlurMargin = 56;

    /// <summary>取屏间隔。读取一次合成结果本身约 17ms（一次合成等待），刻意留出空闲。</summary>
    private const int FrameIntervalMs = 60;

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly Window _window;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopping = new(false);

    private Thread? _thread;
    private IntPtr _windowHandle = IntPtr.Zero;
    private volatile bool _running;
    private volatile bool _paused = true;
    private bool _disposed;
    private int _pendingCommit;

    // 取屏几何（物理像素）。每帧由取屏线程直接用 Win32 读取客户区，
    // 不用 Avalonia 的位置事件：拖动窗口时事件会比画面晚一拍，背景就会整体错位。
    private int _captureX;
    private int _captureY;
    private int _captureWidth;
    private int _captureHeight;

    /// <summary>DIP→物理像素比例与四周外扩的物理像素，仅在 UI 线程写入。</summary>
    private double _scaling = 1;
    private int _marginPixels = (int)BlurMargin;

    // 双缓冲：取屏线程写 _backBuffer，界面读 _frontBuffer，交换在锁内完成。
    private byte[]? _backBuffer;
    private byte[]? _frontBuffer;
    private int _frontLength;
    private PixelSize _frameSize;
    private WriteableBitmap[]? _frames;
    private int _frameSlot;

    public LiveBackdropService(Window window)
    {
        _window = window;
        _window.PositionChanged += OnWindowGeometryChanged;
        _window.SizeChanged += OnWindowGeometryChanged;
        _window.ScalingChanged += OnWindowGeometryChanged;
        _window.PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>最新一帧（双缓冲轮换，实例会变化，界面据此重绘）。</summary>
    public IImage? Frame { get; private set; }

    /// <summary>有新帧可显示；在 UI 线程触发。</summary>
    public event EventHandler? FrameUpdated;

    public bool IsRunning => _running;

    /// <summary>启动取屏。平台不支持或无法排除自身截图时返回 false，调用方退回系统丙烯酸。</summary>
    public bool TryStart()
    {
        if (_disposed) return false;
        if (_running) return true;
        if (!OperatingSystem.IsWindows()) return false;
        if (!_window.IsVisible) return false;

        var handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        // 排除自身是正确性的前提：失败就不做玻璃，避免画面里出现自己。
        if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture)) return false;

        _windowHandle = handle;
        _paused = false;
        RefreshGeometry();
        _stopping.Reset();
        _running = true;

        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "hd2mimnt-glass-backdrop"
        };
        _thread.Start();
        return true;
    }

    /// <summary>停止取屏并恢复窗口可被截屏。</summary>
    public void Stop()
    {
        if (!_running && _windowHandle == IntPtr.Zero) return;
        _running = false;
        _paused = true;
        _stopping.Set();

        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(500); }
            catch (Exception) { /* 关闭时不阻塞 */ }
        }

        Frame = null;
        lock (_gate)
        {
            _frames = null;
            _frameSlot = 0;
            _backBuffer = null;
            _frontBuffer = null;
            _frontLength = 0;
            _frameSize = default;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            try { _ = SetWindowDisplayAffinity(_windowHandle, WdaNone); }
            catch (Exception) { /* 窗口可能已销毁 */ }
            _windowHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _window.PositionChanged -= OnWindowGeometryChanged;
        _window.SizeChanged -= OnWindowGeometryChanged;
        _window.ScalingChanged -= OnWindowGeometryChanged;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _stopping.Dispose();
    }

    private void OnWindowGeometryChanged(object? sender, EventArgs e) => RefreshGeometry();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            _paused = !_window.IsVisible;
            return;
        }

        if (e.Property == Window.WindowStateProperty)
        {
            var minimized = _window.WindowState == WindowState.Minimized;
            if (!minimized) RefreshGeometry();
            _paused = minimized || !_window.IsVisible;
        }
    }

    /// <summary>取屏比窗口多出的一圈，换算成 DIP：界面按它把图层向外偏移，模糊边缘才不取空像素。</summary>
    public double HeadroomDips => _scaling > 0 ? _marginPixels / _scaling : BlurMargin;

    /// <summary>把窗口当前的位置与尺寸换算成物理像素取屏区域。仅在 UI 线程调用。</summary>
    private void RefreshGeometry()
    {
        try
        {
            var scaling = _window.RenderScaling;
            if (scaling <= 0) scaling = 1;

            _scaling = scaling;
            _marginPixels = Math.Max(1, (int)Math.Ceiling(BlurMargin * scaling));
        }
        catch (Exception)
        {
            _captureWidth = 0;
            _captureHeight = 0;
        }
    }

    /// <summary>
    /// 取屏线程每帧读取客户区的真实位置与尺寸（物理像素），并换算成取屏区域。
    /// 用 Win32 而不是 Avalonia 的 <c>PointToScreen</c>/<c>ClientSize</c>：
    /// 后者要在 UI 线程取值、且在窗口拖动/最大化动画期间会落后于真实窗口位置。
    /// </summary>
    private bool TryReadCaptureRegion()
    {
        var handle = _windowHandle;
        if (handle == IntPtr.Zero) return false;

        if (!GetClientRect(handle, out var client)) return false;
        var origin = default(POINT);
        if (!ClientToScreen(handle, ref origin)) return false;

        var margin = _marginPixels;
        _captureX = origin.X - margin;
        _captureY = origin.Y - margin;
        _captureWidth = Math.Max(1, client.Right - client.Left + margin * 2);
        _captureHeight = Math.Max(1, client.Bottom - client.Top + margin * 2);
        return true;
    }

    private void CaptureLoop()
    {
        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var dibBits = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        var allocated = default(PixelSize);

        try
        {
            while (_running)
            {
                if (_paused || !TryReadCaptureRegion() || _captureWidth <= 0 || _captureHeight <= 0)
                {
                    ReleaseSurface();
                    _stopping.Wait(150);
                    continue;
                }

                var size = new PixelSize(_captureWidth, _captureHeight);
                var start = Stopwatch.GetTimestamp();

                if (screenDc == IntPtr.Zero) screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    _stopping.Wait(200);
                    continue;
                }

                if (memoryDc == IntPtr.Zero) memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    _stopping.Wait(200);
                    continue;
                }

                if (size != allocated)
                {
                    if (dib != IntPtr.Zero) DeleteObject(dib);
                    dib = IntPtr.Zero;
                    allocated = default;

                    var info = new BitmapInfo
                    {
                        Header = new BitmapInfoHeader
                        {
                            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                            Width = size.Width,
                            Height = -size.Height,
                            Planes = 1,
                            BitCount = 32,
                            Compression = 0
                        }
                    };
                    dib = CreateDIBSection(memoryDc, ref info, 0, out dibBits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero)
                    {
                        _stopping.Wait(200);
                        continue;
                    }

                    previousObject = SelectObject(memoryDc, dib);
                    allocated = size;
                }

                var captured = StretchBlt(memoryDc, 0, 0, size.Width, size.Height,
                    screenDc, _captureX, _captureY, size.Width, size.Height, SrcCopy);
                if (captured)
                {
                    var length = size.Width * size.Height * 4;
                    EnsureBuffers(length, size);
                    if (Interlocked.CompareExchange(ref _pendingCommit, 1, 0) == 0)
                    {
                        if (_backBuffer is { } buffer && buffer.Length == length)
                        {
                            Marshal.Copy(dibBits, buffer, 0, length);
                            lock (_gate)
                            {
                                (_backBuffer, _frontBuffer) = (_frontBuffer, _backBuffer);
                                _frontLength = length;
                            }

                            PublishFrame(size);
                        }
                        else
                        {
                            Interlocked.Exchange(ref _pendingCommit, 0);
                        }
                    }
                }

                var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                var wait = FrameIntervalMs - (int)elapsed;
                _stopping.Wait(wait > 0 ? wait : 1);
            }
        }
        catch (Exception)
        {
            // 取屏失败只影响观感：下一轮 Apply 会看到 IsRunning=false 并回退到系统丙烯酸。
            _running = false;
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previousObject);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        void ReleaseSurface()
        {
            if (dib == IntPtr.Zero) return;
            if (previousObject != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousObject);
                previousObject = IntPtr.Zero;
            }
            DeleteObject(dib);
            dib = IntPtr.Zero;
            dibBits = IntPtr.Zero;
            allocated = default;
        }
    }

    private void EnsureBuffers(int length, PixelSize size)
    {
        lock (_gate)
        {
            if (_backBuffer is null || _backBuffer.Length != length)
            {
                _backBuffer = new byte[length];
                _frontBuffer = new byte[length];
                _frontLength = 0;
            }

            if (_frameSize != size)
            {
                _frameSize = size;
                _frames = null;
                _frameSlot = 0;
            }
        }
    }

    private void PublishFrame(PixelSize size)
    {
        try
        {
            Dispatcher.UIThread.Post(() => CommitFrame(size), DispatcherPriority.Render);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _pendingCommit, 0);
        }
    }

    private void CommitFrame(PixelSize size)
    {
        try
        {
            if (!_running) return;

            byte[]? front;
            int length;
            WriteableBitmap[] frames;
            lock (_gate)
            {
                front = _frontBuffer;
                length = _frontLength;
                frames = _frames ??= CreateFrames(size);
            }

            if (front is null || length < size.Width * size.Height * 4) return;

            var bitmap = frames[_frameSlot];
            _frameSlot ^= 1;

            using (var locked = bitmap.Lock())
            {
                var rowBytes = Math.Min(locked.Size.Width, size.Width) * 4;
                for (var y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(front, y * size.Width * 4, locked.Address + y * locked.RowBytes, rowBytes);
                }
            }

            Frame = bitmap;
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 尺寸竞态或窗口正在关闭：丢弃该帧。
        }
        finally
        {
            Interlocked.Exchange(ref _pendingCommit, 0);
        }
    }

    private WriteableBitmap[] CreateFrames(PixelSize size)
    {
        var scaling = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        var dpi = new Vector(96 * scaling, 96 * scaling);
        return
        [
            new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul),
            new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul)
        ];
    }

    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int widthDest, int heightDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int widthSrc, int heightSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
}
