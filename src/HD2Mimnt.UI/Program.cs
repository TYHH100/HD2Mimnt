using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI;

class Program
{
    public static IHost? AppHost { get; private set; }
    public static BrowserProtocolHost? ProtocolHost { get; private set; }
    public static string? ProtocolRegistrationError { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        CrashGuard.Install();
        using var protocolHost = new BrowserProtocolHost(BrowserProtocolHost.GetScope());
        var protocolLink = BrowserProtocolHost.GetProtocolArgument(args);
        if (!protocolHost.IsPrimary)
        {
            if (!protocolHost.ForwardAsync(protocolLink).GetAwaiter().GetResult())
                Console.Error.WriteLine("无法将启动请求发送到当前窗口，请关闭旧窗口后重试。");
            return;
        }
        try { WebsiteDownloadLauncher.RegisterCurrentApplication(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ProtocolRegistrationError = "N 网链接接收注册失败，请检查当前用户的注册表写入权限后重启应用。";
        }
        ProtocolHost = protocolHost;
        if (protocolLink.Length != 0) protocolHost.Receive(protocolLink);
        // 构建 Host（在 App.OnFrameworkInitializationCompleted 中初始化）
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
        // Avalonia 已退出：按依赖顺序清理——主 ViewModel 退订事件并排空刷新队列，
        // 再发布最终离线快照，最后停机并释放容器，让所有单例的清理代码真正执行。
        DisposeMainWindowViewModel();
        AppHost?.Services.GetService<HD2Mimnt.Application.Services.OfflineInteropService>()
            ?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (AppHost is { } host)
        {
            try { host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine($"Host stop failed: {ex}"); }
            host.Dispose();
            AppHost = null;
        }
        ProtocolHost = null;
    }

    /// <summary>主窗口 ViewModel；App 启动后赋值，退出时由此处统一释放。</summary>
    public static ViewModels.MainWindowViewModel? MainWindowViewModel { get; internal set; }

    private static void DisposeMainWindowViewModel()
    {
        var viewModel = MainWindowViewModel;
        MainWindowViewModel = null;
        if (viewModel is null) return;
        try { viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"MainWindowViewModel dispose failed: {ex}"); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();

    public static void SetAppHost(IHost host)
    {
        AppHost = host;
    }
}
