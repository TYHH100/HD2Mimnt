using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels.Darktide;

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
        if (!protocolHost.IsPrimary)
        {
            Console.Error.WriteLine("暗潮模组管理器已在运行，请关闭旧窗口后重试。");
            return;
        }
        ProtocolHost = protocolHost;
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
        // Avalonia 已退出：先取消并释放页面操作，再停机并释放容器。
        DisposeDarktideMainWindowViewModel();
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

    /// <summary>暗潮主窗口 ViewModel；启动后赋值，退出时由此处统一释放。</summary>
    public static DarktideMainWindowViewModel? DarktideMainViewModel { get; internal set; }

    private static void DisposeDarktideMainWindowViewModel()
    {
        var viewModel = DarktideMainViewModel;
        DarktideMainViewModel = null;
        if (viewModel is null) return;
        try { viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"DarktideMainWindowViewModel dispose failed: {ex}"); }
    }

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
            .LogToTrace();

    public static void SetAppHost(IHost host)
    {
        AppHost = host;
    }
}
