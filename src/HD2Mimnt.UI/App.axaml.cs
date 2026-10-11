using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Workers;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HD2Mimnt.UI;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    private RuntimeMetricsWriter? _runtimeMetricsWriter;
    private static WindowIcon? _appIcon;
    private static int _splashClosed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 设置依赖注入
            var hostBuilder = Host.CreateDefaultBuilder();
            hostBuilder.ConfigureServices((context, services) => ConfigureServices(services));
            var host = hostBuilder.Build();
            ServiceProvider = host.Services;
            _runtimeMetricsWriter = RuntimeMetricsWriter.StartFromEnvironment();
            desktop.ShutdownRequested += (_, _) => _runtimeMetricsWriter?.Dispose();

            // 设置 Program.AppHost
            Program.SetAppHost(host);

            // 开屏窗口：纯展示，关闭由 Opened 事件与兜底计时器异步触发，
            // 不参与启动流程、没有任何等待关系，结构上不可能与启动互相死锁
            SplashWindow? splash = null;
            long splashShownAt = Stopwatch.GetTimestamp();
            try
            {
                splash = new SplashWindow();
                splash.Show();
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Splash screen failed to show: {ex}");
            }

            // 初始化数据库与 Worker：与历史启动行为一致的全同步序列
            var dbContext = ServiceProvider.GetRequiredService<HD2MimntDbContext>();
            dbContext.InitializeDatabaseAsync().Wait();

            var taskService = ServiceProvider.GetRequiredService<TaskService>();
            var workers = ServiceProvider.GetServices<IWorker>();
            foreach (var worker in workers)
            {
                taskService.RegisterWorker(worker);
            }

            // 创建主窗口，交给框架展示（与历史行为一致）
            var mainViewModel = ServiceProvider.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
                Icon = TryGetAppIcon()
            };
            mainViewModel.AttachOwner(mainWindow);
            desktop.MainWindow = mainWindow;

            if (splash != null)
            {
                // 主窗口展示后关闭开屏（最短展示 1.5s）；另有 6s 兜底强制关闭，
                // 两条路径都只是"往队列里塞一个关窗动作"，先到者生效、互不等待
                mainWindow.Opened += (_, _) =>
                    _ = CloseSplashAsync(desktop, splash, splashShownAt, minDisplay: true);
                _ = CloseSplashAsync(desktop, splash, splashShownAt, forcedDelay: TimeSpan.FromSeconds(6));
            }

            // 初始化 ViewModel - 使用异步启动避免死锁
            _ = InitializeMainWindowAsync(mainViewModel, mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeMainWindowAsync(MainWindowViewModel main, MainWindow window)
    {
        await main.InitializeAsync();
        if (Program.ProtocolRegistrationError is { } error)
            ServiceProvider?.GetRequiredService<NotificationService>().ShowError(error, "N 网链接接收");
        Program.ProtocolHost?.SetReceiver(value => Dispatcher.UIThread.Post(() =>
        {
            if (!window.IsVisible) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            main.ReceiveBrowserProtocolLink(value);
        }));
    }

    /// <summary>关闭开屏；两个触发路径（Opened / 兜底计时器）先到者生效，保证幂等。</summary>
    private static async Task CloseSplashAsync(
        IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash,
        long splashShownAt, bool minDisplay = false, TimeSpan forcedDelay = default)
    {
        if (minDisplay)
        {
            // 最短展示 1.5s，数据库初始化太快时避免一闪而过看不到 logo
            var elapsed = Stopwatch.GetElapsedTime(splashShownAt);
            var minimum = TimeSpan.FromMilliseconds(1500);
            if (elapsed < minimum)
            {
                await Task.Delay(minimum - elapsed);
            }
        }
        else if (forcedDelay > TimeSpan.Zero)
        {
            await Task.Delay(forcedDelay);
        }

        if (Interlocked.Exchange(ref _splashClosed, 1) != 0)
        {
            return;
        }

        // Background 优先级晚于首帧渲染，无黑闪；即使启动期 UI 繁忙也只会延后、不会丢失
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                splash.Close();
                // 调试器启动的进程拿不到前台激活权限，显式把主窗口提到最前
                desktop.MainWindow?.Activate();
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            },
            DispatcherPriority.Background);
    }

    internal static WindowIcon? TryGetAppIcon()
    {
        if (_appIcon == null)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://HD2Mimnt.UI/Assets/logo.png"));
                _appIcon = new WindowIcon(stream);
            }
            catch
            {
                // 资产缺失时不阻塞启动
            }
        }

        return _appIcon;
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 获取应用程序数据目录
        var configuredDataPath = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        var appDataPath = string.IsNullOrWhiteSpace(configuredDataPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HD2Mimnt")
            : Path.GetFullPath(configuredDataPath);
        Directory.CreateDirectory(appDataPath);

        var dbPath = Path.Combine(appDataPath, "hd2mimnt.db");
        var modsStoragePath = Path.Combine(appDataPath, "mods");
        var imageCachePath = Path.Combine(appDataPath, "ImageCache");
        Directory.CreateDirectory(modsStoragePath);
        Directory.CreateDirectory(imageCachePath);

        // 日志（可选）
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLogProvider(Path.Combine(appDataPath, "Logs", "application.log")));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // 基础设施层
        services.AddSingleton(sp => new HD2MimntDbContext(dbPath));
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IExternalToolRepository, ExternalToolRepository>();
        services.AddSingleton<IPluginRepository, PluginRepository>();
        services.AddSingleton<IPluginPermissionRepository, PluginPermissionRepository>();
        services.AddSingleton<IPluginExecutionRepository, PluginExecutionRepository>();
        services.AddSingleton<IPlatformCredentialStore, PlatformCredentialStore>();
        services.AddSingleton<IPluginHostManager, PluginHostManager>();

        // 图片服务
        services.AddSingleton<IImageCacheService>(sp =>
            new ImageCacheService(imageCachePath, memoryCacheSize: 100));
        services.AddSingleton<IImageLoaderService, ImageLoaderService>();

        // Dedicated archive client: do not share platform API authentication headers.
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("downloads", (_, _) =>
        {
            var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HD2Mimnt-ModManager");
            return client;
        });
        services.AddSingleton<IHttpDownloadService>(sp => new HttpDownloadService(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("downloads"),
            sp.GetRequiredService<IDownloadOptionsProvider>(), Path.Combine(appDataPath, "Downloads")));
        services.AddSingleton<DownloadSettingsService>();
        services.AddSingleton<IDownloadOptionsProvider>(sp => sp.GetRequiredService<DownloadSettingsService>());
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("nexus-api", (_, _) => new System.Net.Http.HttpClient());
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("github-api", (_, _) => new System.Net.Http.HttpClient());
        services.AddSingleton(sp => new NexusModsPlatformAdapter(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("nexus-api"),
            sp.GetRequiredService<IHttpDownloadService>(), typeof(App).Assembly.GetName().Version!.ToString(3)));
        services.AddSingleton(sp => new GitHubPlatformAdapter(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("github-api"), sp.GetRequiredService<IHttpDownloadService>()));
        services.AddSingleton<IPlatformRegistry>(sp =>
        {
            var registry = new PlatformRegistry();
            registry.RegisterPlatform(sp.GetRequiredService<NexusModsPlatformAdapter>());
            registry.RegisterPlatform(sp.GetRequiredService<GitHubPlatformAdapter>());
            return registry;
        });
        services.AddSingleton<DownloadManagerService>();
        services.AddSingleton<NexusModsTestService>();
        services.AddSingleton<ModLinkService>();
        services.AddSingleton<ModOnlineService>();
        services.AddSingleton<IWebsiteDownloadLauncher, WebsiteDownloadLauncher>();

        // 应用层
        services.AddSingleton(sp => new ModService(
            sp.GetRequiredService<IModRepository>(),
            modsStoragePath,
            sp.GetService<ILogger<ModService>>(),
            sp.GetRequiredService<DeploymentService>()));
        services.AddSingleton(sp => new TestDataService(
            sp.GetRequiredService<ModService>()));

        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>()));

        services.AddSingleton<ConflictDetectionService>();

        services.AddSingleton<IExternalToolService>(sp => new ExternalToolService(
            sp.GetRequiredService<IExternalToolRepository>(),
            sp.GetRequiredService<ILogger<ExternalToolService>>()));
        services.AddSingleton<IPluginService, PluginService>();

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IModRepository>(),
            sp.GetService<ILogger<SearchService>>()));

        // Task Center
        services.AddSingleton<TaskService>();

        // Workers
        services.AddSingleton<IWorker, ImportWorker>();
        services.AddSingleton<IWorker, ScanWorker>();
        services.AddSingleton<IWorker, ValidationWorker>();

        // 部署相关（补充缺失的注册）
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();

        // 注册三种部署方法
        services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();

        services.AddSingleton(sp => new DeploymentService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IDeploymentRepository>(),
            sp.GetServices<IDeploymentMethod>(),
            sp.GetService<ILogger<DeploymentService>>()));

        // 新增核心服务
        services.AddSingleton(sp => new SettingsService(
            appDataPath,
            sp.GetService<ILogger<SettingsService>>()));

        services.AddSingleton(sp => new BackupService(
            appDataPath,
            sp.GetRequiredService<HD2MimntDbContext>(),
            sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IFileLogService>(new FileLogService(appDataPath));

        services.AddSingleton(sp => new GameDetectionService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetService<ILogger<GameDetectionService>>()));

        services.AddSingleton<NotificationService>();
        services.AddSingleton<ConfirmationService>();
        services.AddSingleton<IConfirmationService>(sp => sp.GetRequiredService<ConfirmationService>());
        services.AddSingleton<ImagePreviewService>();

        // UI 层 ViewModels
        services.AddTransient(sp => new LibraryViewModel(
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<SearchService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<DeploymentService>(),
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<IConfirmationService>(),
            sp.GetRequiredService<ImagePreviewService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ModLinkService>(),
            sp.GetRequiredService<ModOnlineService>(),
            sp.GetRequiredService<IWebsiteDownloadLauncher>()));

        services.AddTransient(sp => new ProfileViewModel(
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<ConfirmationService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ImagePreviewService>()));

        services.AddTransient(sp => new ConflictsViewModel(
            sp.GetRequiredService<ConflictDetectionService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<NotificationService>()));

        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IImageCacheService>()));
        services.AddTransient(sp => new TaskCenterViewModel(
            sp.GetRequiredService<TaskService>()));
        services.AddTransient(sp => new ExternalToolsViewModel(
            sp.GetRequiredService<IExternalToolService>(),
            sp.GetRequiredService<ConfirmationService>()));
        services.AddTransient(sp => new PluginManagementViewModel(
            sp.GetRequiredService<IPluginService>(),
            sp.GetRequiredService<ConfirmationService>()));
        services.AddTransient(sp => new BackupViewModel(
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<ConfirmationService>(),
            sp.GetRequiredService<NotificationService>()));
        services.AddTransient(sp => new LogViewModel(
            sp.GetRequiredService<IFileLogService>()));
        services.AddTransient(sp => new NotificationPanelViewModel(
            sp.GetRequiredService<NotificationService>()));

        services.AddTransient(sp => new MainWindowViewModel(
            sp.GetRequiredService<LibraryViewModel>(),
            sp.GetRequiredService<ProfileViewModel>(),
            sp.GetRequiredService<ConflictsViewModel>(),
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<TaskService>(),
            sp.GetRequiredService<TestDataService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<TaskCenterViewModel>(),
            sp.GetRequiredService<NotificationPanelViewModel>(),
            sp.GetRequiredService<ExternalToolsViewModel>(),
            sp.GetRequiredService<PluginManagementViewModel>(),
            sp.GetRequiredService<BackupViewModel>(),
            sp.GetRequiredService<LogViewModel>(),
            sp.GetRequiredService<ConfirmationService>(),
            sp.GetRequiredService<ImagePreviewService>(),
            sp.GetRequiredService<NexusModsTestService>()));
    }
}
