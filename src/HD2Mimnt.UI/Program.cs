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
        // Avalonia 已退出，在后台发布最终离线快照并等清理结束，避免退出时遗留临时文件。
        AppHost?.Services.GetService<HD2Mimnt.Application.Services.OfflineInteropService>()
            ?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ProtocolHost = null;
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
