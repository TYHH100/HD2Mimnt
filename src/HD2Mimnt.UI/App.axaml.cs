using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Application.Workers;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Localization;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Storage;
using HD2Mimnt.Infrastructure.Migration;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Views;
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
    private StorageSettingsService? _startupStorage;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = InitializeDesktopAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var cancellation = new CancellationTokenSource();
        var running = true;
        StorageStartupWindow? startup = null;

        void ShowStorageStartupWindow()
        {
            startup = new StorageStartupWindow();
            desktop.MainWindow = startup;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            startup.CancelRequested += (_, _) =>
            {
                if (running) cancellation.Cancel();
                else { startup.Close(); desktop.Shutdown(1); }
            };
            startup.Closing += (_, e) =>
            {
                if (!running) return;
                e.Cancel = true;
                cancellation.Cancel();
            };
            desktop.ShutdownRequested += (_, e) =>
            {
                if (!running) return;
                e.Cancel = true;
                cancellation.Cancel();
            };
            startup.Closed += (_, _) =>
            {
                if (!running && desktop.MainWindow == startup) desktop.Shutdown(1);
            };
            startup.Show();
        }

        try
        {
            _startupStorage = new StorageSettingsService(new StorageLocationStore());
            if (_startupStorage.HasPendingMigration) ShowStorageStartupWindow();
            await _startupStorage.InitializeAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // 设置依赖注入
            var hostBuilder = Host.CreateDefaultBuilder();
            hostBuilder.ConfigureServices((context, services) => ConfigureServices(services));
            var host = hostBuilder.Build();
            ServiceProvider = host.Services;
            _runtimeMetricsWriter = RuntimeMetricsWriter.StartFromEnvironment();
            desktop.ShutdownRequested += (_, _) => _runtimeMetricsWriter?.Dispose();

            // 设置 Program.AppHost
            Program.SetAppHost(host);

            // 在 ViewModel/窗口构造前应用保存的语言，自动模式持续跟随系统。
            var localization = ServiceProvider.GetRequiredService<LocalizationService>();
            ServiceProvider.GetRequiredService<UiLocalization>();
            localization.StartMonitoring();
            desktop.Exit += (_, _) => localization.Dispose();

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

            // 初始化数据库与 Worker；异步启动后不能在 UI 上同步等待数据库续体。
            var dbContext = ServiceProvider.GetRequiredService<HD2MimntDbContext>();
            await dbContext.InitializeDatabaseAsync(cancellation.Token);

            var taskService = ServiceProvider.GetRequiredService<TaskService>();
            var workers = ServiceProvider.GetServices<IWorker>();
            foreach (var worker in workers)
            {
                taskService.RegisterWorker(worker);
            }

            // 创建并展示主窗口。
            var mainViewModel = ServiceProvider.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
                Icon = TryGetAppIcon()
            };
            mainViewModel.AttachOwner(mainWindow);
            desktop.MainWindow = mainWindow;
            running = false;
            startup?.Close();
            if (splash == null) desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            if (splash != null)
            {
                // 主窗口展示后关闭开屏（最短展示 1.5s）；另有 6s 兜底强制关闭，
                // 两条路径都只是"往队列里塞一个关窗动作"，先到者生效、互不等待
                mainWindow.Opened += (_, _) =>
                    _ = CloseSplashAsync(desktop, splash, splashShownAt, minDisplay: true);
                _ = CloseSplashAsync(desktop, splash, splashShownAt, forcedDelay: TimeSpan.FromSeconds(6));
            }
            mainWindow.Show();

            // 初始化 ViewModel - 使用异步启动避免死锁
            _ = InitializeMainWindowAsync(mainViewModel, mainWindow);
        }
        catch (OperationCanceledException)
        {
            running = false;
            startup?.Close();
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            running = false;
            if (startup == null) ShowStorageStartupWindow();
            startup!.ShowFailure(UiLocalization.Current.Format("Settings.Storage.StartupFailed", ex.Message));
        }
    }

    private static async Task InitializeMainWindowAsync(MainWindowViewModel main, MainWindow window)
    {
        await main.InitializeAsync();
        if (ServiceProvider?.GetRequiredService<StorageSettingsService>().StartupMessage is { } storageMessage)
            ServiceProvider.GetRequiredService<NotificationService>().ShowWarning(storageMessage, UiLocalization.Current["Settings.Storage.Title"]);
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
        // 先完成上次启动留下的搬迁，再按已提交的目录创建服务。
        var storageSettings = _startupStorage ?? new StorageSettingsService(new StorageLocationStore());
        var storageLocations = storageSettings.Current;
        var appDataPath = storageLocations.AppDataPath;
        Directory.CreateDirectory(appDataPath);

        var dbPath = storageLocations.DatabasePath;
        var modsStoragePath = storageLocations.ModsPath;
        var imageCachePath = storageLocations.ImageCachePath;
        Directory.CreateDirectory(modsStoragePath);
        Directory.CreateDirectory(imageCachePath);
        Directory.CreateDirectory(storageLocations.DownloadsPath);
        Directory.CreateDirectory(storageLocations.TempPath);
        services.AddSingleton(storageSettings);

        // 日志（可选）
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLogProvider(Path.Combine(appDataPath, "Logs", "application.log")));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // 基础设施层
        services.AddSingleton(sp => new HD2MimntDbContext(dbPath));
        services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(dbPath));
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
            sp.GetRequiredService<IDownloadOptionsProvider>(), storageLocations.DownloadsPath, storageLocations.TempPath));
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
            sp.GetRequiredService<DeploymentService>(), storageLocations.TempPath));
        services.AddSingleton(sp => new TestDataService(
            sp.GetRequiredService<ModService>()));

        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>()));

        // 外部管理器迁移：读取器只读源数据，实际复制和落库由 Application 服务完成。
        services.AddSingleton<IExternalManagerReader, Helldivers2ModManagerReader>();
        services.AddSingleton<IExternalManagerReader, EchelonReader>();
        services.AddSingleton<IExternalManagerReader, Hd2ArsenalReader>();
        services.AddSingleton<IExternalManagerWriter, Helldivers2ModManagerWriter>();
        services.AddSingleton<IExternalManagerWriter, EchelonWriter>();
        services.AddSingleton<IExternalManagerWriter, Hd2ArsenalWriter>();
        services.AddSingleton<ExternalManagerExportService>();
        services.AddTransient<ManagerMigrationViewModel>();
        services.AddSingleton(sp => new ExternalManagerMigrationService(
            sp.GetServices<IExternalManagerReader>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetService<ILogger<ExternalManagerMigrationService>>()));

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
            sp.GetRequiredService<ISettingsRepository>(),
            appDataPath,
            sp.GetService<ILogger<SettingsService>>()));

        services.AddSingleton<ISystemLanguageProvider, SystemLanguageProvider>();
        services.AddSingleton<TranslationCatalog>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton(sp =>
        {
            var adapter = UiLocalization.Current;
            adapter.Attach(sp.GetRequiredService<ILocalizationService>());
            return adapter;
        });

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
            sp.GetRequiredService<IConfirmationService>(),
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
            sp.GetRequiredService<IImageCacheService>(),
            sp.GetRequiredService<UiLocalization>(),
            sp.GetRequiredService<StorageSettingsService>()));
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
            sp.GetRequiredService<NexusModsTestService>(),
            sp.GetRequiredService<ManagerMigrationViewModel>()));
    }
}
