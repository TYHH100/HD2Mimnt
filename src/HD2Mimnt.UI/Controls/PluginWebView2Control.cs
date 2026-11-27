using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using Microsoft.Web.WebView2.Core;

namespace HD2Mimnt.UI.Controls;

/// <summary>把受控的本地插件页面嵌入 Avalonia 页面的 WebView2 原生宿主。</summary>
public sealed class PluginWebView2Control : NativeControlHost
{
    private const int WsChild = 0x40000000;
    private const int WsClipChildren = 0x02000000;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private IntPtr _handle;
    private int _generation;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private PluginWebUiMessageBridge? _bridge;
    private WebSurfaceConfiguration? _configuration;
    private bool _isPageReady;
    private bool _isHostVisible = true;

    public event EventHandler<string?>? ErrorChanged;

    public PluginWebView2Control()
    {
        SizeChanged += (_, _) => UpdateBounds();
    }

    public void Configure(
        string pluginId,
        string surfaceId,
        string? entryPath,
        PluginWebView2Environment? environment,
        Func<string, IReadOnlyDictionary<string, JsonElement>, Task<PluginOperationResult>> execute)
    {
        if (_configuration is { } current
            && current.PluginId == pluginId
            && current.SurfaceId == surfaceId
            && current.EntryPath == entryPath
            && ReferenceEquals(current.Environment, environment))
        {
            return;
        }

        CloseController();
        _configuration = new WebSurfaceConfiguration(pluginId, surfaceId, entryPath, environment, execute);
        ErrorChanged?.Invoke(this, null);
        if (_handle != IntPtr.Zero)
        {
            StartInitialization(_handle, Interlocked.Increment(ref _generation));
        }
    }

    public void SetHostVisible(bool visible)
    {
        _isHostVisible = visible;
        UpdateVisibility();
        Dispatcher.UIThread.Post(UpdateVisibility);
    }

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        CloseController();
        _configuration = null;
    }

    public void Reload()
    {
        ErrorChanged?.Invoke(this, null);
        if (_webView is not null)
        {
            _webView.Reload();
            return;
        }

        if (_handle != IntPtr.Zero)
        {
            CloseController();
            StartInitialization(_handle, Interlocked.Increment(ref _generation));
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);

        _handle = CreateWindowEx(0, "STATIC", string.Empty, WsChild | WsClipChildren,
            0, 0, 100, 100, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"创建 WebView2 宿主窗口失败：{Marshal.GetLastWin32Error()}");
        }

        StartInitialization(_handle, Interlocked.Increment(ref _generation));
        return new PlatformHandle(_handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Interlocked.Increment(ref _generation);
        CloseController();
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return result;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateVisibility();
        }
    }

    private void StartInitialization(IntPtr handle, int generation)
    {
        SafeAsync.FireAndForget(InitializeAsync(handle, generation), "初始化插件 WebView2 页面");
    }

    private async Task InitializeAsync(IntPtr handle, int generation)
    {
        CoreWebView2Controller? controller = null;
        try
        {
            var configuration = _configuration;
            if (configuration is null) return;
            if (string.IsNullOrWhiteSpace(configuration.EntryPath) || !File.Exists(configuration.EntryPath))
            {
                throw new InvalidDataException(UiLocalization.Current["PluginWebView.EntryMissing"]);
            }
            if (configuration.Environment is null)
            {
                throw new InvalidOperationException(UiLocalization.Current["PluginWebView.EnvironmentUnavailable"]);
            }

            var environment = await configuration.Environment.GetAsync();
            var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
            controllerOptions.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            controller = await environment.CreateCoreWebView2ControllerAsync(handle, controllerOptions);
            if (!IsCurrent(generation, handle))
            {
                controller.Close();
                return;
            }

            _controller = controller;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            var webView = controller.CoreWebView2;
            _webView = webView;
            var settings = webView.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;

            var webRoot = Path.GetDirectoryName(configuration.EntryPath)!;
            var hostName = PluginWebUiMessageBridge.CreateHostName(configuration.PluginId, configuration.SurfaceId);
            webView.SetVirtualHostNameToFolderMapping(
                hostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _bridge = new PluginWebUiMessageBridge(
                hostName,
                configuration.PluginId,
                configuration.SurfaceId,
                PostMessage,
                configuration.Execute);
            webView.WebMessageReceived += OnWebMessageReceived;
            webView.NavigationStarting += OnNavigationStarting;
            webView.FrameNavigationStarting += OnFrameNavigationStarting;
            webView.NewWindowRequested += OnNewWindowRequested;
            webView.DownloadStarting += OnDownloadStarting;
            webView.PermissionRequested += OnPermissionRequested;
            webView.ProcessFailed += OnProcessFailed;
            webView.NavigationCompleted += OnNavigationCompleted;
            webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            webView.WebResourceRequested += OnWebResourceRequested;

            await webView.AddScriptToExecuteOnDocumentCreatedAsync(PluginWebUiMessageBridge.BridgeScript);
            if (!IsCurrent(generation, handle))
            {
                CloseController();
                return;
            }

            var relativeEntry = Path.GetRelativePath(webRoot, configuration.EntryPath).Replace('\\', '/');
            var escapedEntry = string.Join('/', relativeEntry.Split('/').Select(Uri.EscapeDataString));
            webView.Navigate($"https://{hostName}/{escapedEntry}");
            UpdateBounds();
            UpdateVisibility();
            ErrorChanged?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            if (controller is not null && !ReferenceEquals(controller, _controller))
            {
                controller.Close();
            }
            if (!IsCurrent(generation, handle)) return;

            CloseController();
            var message = ex is WebView2RuntimeNotFoundException
                ? UiLocalization.Current["PluginWebView.RuntimeMissing"]
                : ex.Message;
            ErrorChanged?.Invoke(this, message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        _bridge?.HandleMessage(e.Source, e.WebMessageAsJson);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _bridge?.CancelPending();
        if (_bridge is null || !PluginWebUiMessageBridge.IsAllowedOrigin(e.Uri, _bridge.HostName))
        {
            e.Cancel = true;
            return;
        }
        if (e.NavigationKind is CoreWebView2NavigationKind.NewDocument or CoreWebView2NavigationKind.Reload)
        {
            _isPageReady = false;
            UpdateVisibility();
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_bridge is null || !PluginWebUiMessageBridge.IsAllowedOrigin(e.Uri, _bridge.HostName))
        {
            e.Cancel = true;
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 webView || _bridge is null || _configuration is null) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
        {
            e.Response = webView.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            return;
        }
        if (!PluginWebUiMessageBridge.IsAllowedOrigin(uri.AbsoluteUri, _bridge.HostName))
        {
            if (PluginWebUiMessageBridge.IsAllowedResource(uri, _bridge.HostName)) return;
            e.Response = webView.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            return;
        }

        e.Response = CreateResourceResponse(webView, uri);
    }

    private CoreWebView2WebResourceResponse CreateResourceResponse(CoreWebView2 webView, Uri uri)
    {
        var webRoot = Path.GetDirectoryName(_configuration!.EntryPath)!;
        try
        {
            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            {
                throw new InvalidDataException("Invalid resource path.");
            }
            var path = PathGuard.ResolveInsideRoot(webRoot, Path.Combine(webRoot, relativePath));
            if (!File.Exists(path)) throw new FileNotFoundException();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var contentType = extension switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" or ".mjs" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".ico" => "image/x-icon",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream"
            };
            var headers = $"Content-Type: {contentType}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store";
            if (extension is ".html" or ".htm")
            {
                headers += "\r\nContent-Security-Policy: default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; worker-src 'none'; frame-src 'self'; form-action 'none'; base-uri 'self'";
            }
            return webView.Environment.CreateWebResourceResponse(File.OpenRead(path), 200, "OK", headers);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return webView.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        => e.State = CoreWebView2PermissionState.Deny;

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _isPageReady = false;
        UpdateVisibility();
        ErrorChanged?.Invoke(this, string.Format(
            UiLocalization.Current["PluginWebView.ProcessFailed"],
            e.ProcessFailedKind));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _isPageReady = false;
            UpdateVisibility();
            ErrorChanged?.Invoke(this, string.Format(
                UiLocalization.Current["PluginWebView.NavigationFailed"],
                e.WebErrorStatus));
            return;
        }

        _isPageReady = true;
        ErrorChanged?.Invoke(this, null);
        UpdateVisibility();
    }

    private void PostMessage(string json)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            PostMessageCore(json);
            return;
        }
        Dispatcher.UIThread.Post(() => PostMessageCore(json));
    }

    private void PostMessageCore(string json)
    {
        if (!IsCurrent(_generation, _handle)) return;
        try
        {
            _webView?.PostWebMessageAsJson(json);
        }
        catch (COMException)
        {
            // 页面正在导航或关闭时，回执可能已无法送达。
        }
    }

    private void UpdateVisibility()
    {
        var shouldShow = IsVisible && IsEffectivelyVisible && _isHostVisible && _isPageReady;
        if (_controller is not null)
        {
            _controller.IsVisible = shouldShow;
        }
        if (_handle != IntPtr.Zero)
        {
            ShowWindow(_handle, shouldShow ? SwShow : SwHide);
        }
    }

    private void UpdateBounds()
    {
        if (_handle == IntPtr.Zero) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        MoveWindow(_handle, 0, 0, width, height, true);
        if (_controller is not null && GetClientRect(_handle, out var rect))
        {
            _controller.RasterizationScale = (float)scaling;
            _controller.Bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    private bool IsCurrent(int generation, IntPtr handle)
        => generation == Volatile.Read(ref _generation) && handle == _handle && handle != IntPtr.Zero;

    private void CloseController()
    {
        _isPageReady = false;
        if (_handle != IntPtr.Zero)
        {
            ShowWindow(_handle, SwHide);
        }
        _bridge?.CancelPending();
        _bridge = null;
        _webView = null;
        _controller?.Close();
        _controller = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_handle != IntPtr.Zero)
        {
            ShowWindow(_handle, SwHide);
        }
        Interlocked.Increment(ref _generation);
        CloseController();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_handle != IntPtr.Zero && _controller is null)
        {
            StartInitialization(_handle, Interlocked.Increment(ref _generation));
        }
        UpdateVisibility();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record WebSurfaceConfiguration(
        string PluginId,
        string SurfaceId,
        string? EntryPath,
        PluginWebView2Environment? Environment,
        Func<string, IReadOnlyDictionary<string, JsonElement>, Task<PluginOperationResult>> Execute);
}
