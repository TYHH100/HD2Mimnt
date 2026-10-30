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
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Diagnostics;
using HD2Mimnt.Infrastructure.Analysis;
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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HD2Mimnt.UI;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    private RuntimeMetricsWriter? _runtimeMetricsWriter;
    private static WindowIcon? _appIcon;
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
            CrashGuard.AttachLogging(host.Services);

            // 在 ViewModel/窗口构造前应用保存的语言，自动模式持续跟随系统。
            var localization = ServiceProvider.GetRequiredService<LocalizationService>();
            ServiceProvider.GetRequiredService<UiLocalization>();
            localization.StartMonitoring();
            desktop.Exit += (_, _) => localization.Dispose();

            // 初始化数据库与 Worker；异步启动后不能在 UI 上同步等待数据库续体。
            var dbContext = ServiceProvider.GetRequiredService<HD2MimntDbContext>();
            await dbContext.InitializeDatabaseAsync(cancellation.Token);

            // 旧会话式部署记录先收养为当前安装状态，再做离线快照/UI 读取。
            try
            {
                await ServiceProvider.GetRequiredService<DeploymentStateMigrationService>().EnsureMigratedAsync(
                    ServiceProvider.GetRequiredService<SettingsService>().GetEffectiveGamePath(), cancellation.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceProvider.GetService<ILogger<DeploymentStateMigrationService>>()
                    ?.LogWarning(ex, "部署状态迁移失败，继续使用原记录");
            }

            var taskService = ServiceProvider.GetRequiredService<TaskService>();
            var workers = ServiceProvider.GetServices<IWorker>();
            foreach (var worker in workers)
            {
                taskService.RegisterWorker(worker);
            }

            // 创建并展示主窗口。
            var mainViewModel = ServiceProvider.GetRequiredService<MainWindowViewModel>();
            Program.MainWindowViewModel = mainViewModel;
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
                Icon = TryGetAppIcon()
            };
            mainViewModel.AttachOwner(mainWindow);
            desktop.MainWindow = mainWindow;
            running = false;
            startup?.Close();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
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
            var detail = ex is UnauthorizedAccessException
                ? UiLocalization.Current.Format("Settings.Storage.AccessDenied", ex.Message)
                : ex.Message;
            startup!.ShowFailure(UiLocalization.Current.Format("Settings.Storage.StartupFailed", detail));
        }
    }

    private static async Task InitializeMainWindowAsync(MainWindowViewModel main, MainWindow window)
    {
        // 窗口已显示后才执行初始化，任何失败必须可见，不能静默丢失。
        try
        {
            await main.InitializeAsync();
            var interop = ServiceProvider!.GetRequiredService<OfflineInteropService>();
            await interop.StartAsync();
            if (interop.LastError is { } interopError)
                ServiceProvider!.GetRequiredService<NotificationService>().ShowWarning(interopError, UiLocalization.Current["Interop.Title"]);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashGuard.Report(ex, "主窗口初始化失败");
        }
    }

    internal static WindowIcon? TryGetAppIcon()
    {
        if (_appIcon == null)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://HD2Mimnt.UI/Assets/app.ico"));
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
        // 加载器识别配置：应用目录 Config/LoaderCatalog.json 默认 + <AppData>/loaders.json 覆盖，规则变化由 ruleVersion 驱动重扫。
        HD2Mimnt.Infrastructure.Analysis.LoaderRegistry.Initialize(appDataPath);

        var dbPath = storageLocations.DatabasePath;
        var modsStoragePath = storageLocations.ModsPath;
        var imageCachePath = storageLocations.ImageCachePath;
        Directory.CreateDirectory(modsStoragePath);
        Directory.CreateDirectory(imageCachePath);
        Directory.CreateDirectory(storageLocations.DownloadsPath);
        Directory.CreateDirectory(storageLocations.TempPath);
        services.AddSingleton(storageSettings);

        var diagnosticLogSettings = new DiagnosticLogSettings();
        services.AddSingleton(diagnosticLogSettings);
        // 筛选只读内存快照，避免记录设置日志时重新获取设置锁。
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLogProvider(Path.Combine(appDataPath, "Logs", "application.log")));
            builder.AddFilter((_, level) => diagnosticLogSettings.IsEnabled(level));
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
        services.AddSingleton<IDirectDownloadClientFactory>(sp => new DirectDownloadClientFactory(
            sp.GetRequiredService<IDownloadOptionsProvider>(), storageLocations.DownloadsPath, storageLocations.TempPath));
        services.AddSingleton(sp => new DirectDownloadService(sp.GetRequiredService<IDirectDownloadClientFactory>(),
            sp.GetRequiredService<IDownloadOptionsProvider>(), sp.GetRequiredService<SettingsService>(), storageLocations.TempPath));
        services.AddTransient<DirectDownloadViewModel>();
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("nexus-api", (_, _) => new System.Net.Http.HttpClient());
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("github-api", (_, _) => new System.Net.Http.HttpClient());
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("ayaka-api", (_, _) =>
            new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton(sp => new HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("ayaka-api"), sp.GetRequiredService<IDownloadOptionsProvider>(),
            storageLocations.DownloadsPath, storageLocations.TempPath,
            logger: sp.GetRequiredService<ILogger<HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter>>()));
        services.AddSingleton<AyakaModsAuthService>();
        services.AddSingleton(new AyakaModsOAuthOptions
        { ClientId = Environment.GetEnvironmentVariable("HD2MIMNT_AYAKA_CLIENT_ID")?.Trim() ?? "" });
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("ayaka-oauth", (_, _) =>
            new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IAyakaModsOAuthClient>(sp => new HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsOAuthClient(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("ayaka-oauth")));
        services.AddSingleton<AyakaModsOAuthService>();
        services.AddSingleton<AyakaModsAccountViewModel>();
        services.AddSingleton(sp => new NexusModsPlatformAdapter(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("nexus-api"),
            sp.GetRequiredService<IHttpDownloadService>(), typeof(App).Assembly.GetName().Version!.ToString(3),
            sp.GetRequiredService<ILogger<NexusModsPlatformAdapter>>()));
        services.AddSingleton(sp => new GitHubPlatformAdapter(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("github-api"), sp.GetRequiredService<IHttpDownloadService>()));
        services.AddSingleton<IPlatformRegistry>(sp =>
        {
            var registry = new PlatformRegistry();
            registry.RegisterPlatform(sp.GetRequiredService<NexusModsPlatformAdapter>());
            registry.RegisterPlatform(sp.GetRequiredService<GitHubPlatformAdapter>());
            registry.RegisterPlatform(sp.GetRequiredService<HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter>());
            return registry;
        });
        services.AddSingleton<DownloadManagerService>();
        services.AddSingleton(NexusModsOAuthConfiguration.Create());
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("nexus-oauth", (_, _) =>
            new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<INexusModsOAuthClient>(sp =>
            new HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsOAuthClient(
                sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("nexus-oauth"),
                applicationVersion: typeof(App).Assembly.GetName().Version!.ToString(3)));
        services.AddSingleton<NexusModsTestService>();
        services.AddSingleton<NexusModsAuthService>(sp => sp.GetRequiredService<NexusModsTestService>());
        services.AddTransient<NexusModsAccountViewModel>();
        services.AddSingleton<ModLinkService>();
        services.AddSingleton<ModOnlineService>();
        services.AddSingleton(_ => new BrowserDownloadHandoffService(HD2Mimnt.Infrastructure.BrowserDownloadsDirectory.GetPath()));
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("browse-images", (_, _) => new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        services.AddSingleton(sp => new ModBrowseService(sp.GetRequiredService<IPlatformRegistry>(),
            sp.GetRequiredService<DownloadManagerService>(), sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("browse-images"), storageLocations.TempPath));
        services.AddTransient<BrowseModsViewModel>();
        services.AddSingleton<ModCreationService>();
        services.AddTransient<ModCreationViewModel>();
        services.AddSingleton<IWebsiteDownloadLauncher, WebsiteDownloadLauncher>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();

        // 应用层
        services.AddSingleton(sp => new ModService(
            sp.GetRequiredService<IModRepository>(),
            modsStoragePath,
            sp.GetService<ILogger<ModService>>(),
            sp.GetRequiredService<DeploymentService>(), storageLocations.TempPath,
            sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<ProfileService>()));
        services.AddSingleton(sp => new TestDataService(
            sp.GetRequiredService<ModService>()));

        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>()));

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
        services.AddSingleton<IModCompatibilityAnalyzer, ModCompatibilityAnalyzer>();
        services.AddSingleton<ICompatibilityResultStore, CompatibilityResultRepository>();
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("compatibility-names", (_, _) => new System.Net.Http.HttpClient
            { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<ICompatibilityResourceNames>(sp => new CompatibilityResourceNameProvider(
            sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("compatibility-names"), storageLocations.TempPath));
        services.AddSingleton<IGameUnitReferenceReader>(_ => new GameUnitReferenceReader(storageLocations.CachePath));
        services.AddSingleton<IGameSoundBankReader>(sp => (GameUnitReferenceReader)sp.GetRequiredService<IGameUnitReferenceReader>());
        services.AddSingleton<HD2Mimnt.Infrastructure.SoundFix.SoundFixInspector>();
        services.AddSingleton(sp => new SoundFixService(sp.GetRequiredService<IModRepository>(), sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<HD2Mimnt.Infrastructure.SoundFix.SoundFixInspector>(), sp.GetRequiredService<SettingsService>(),
            Path.Combine(storageLocations.AppDataPath, "SoundFixBackups")));
        services.AddTransient<SoundFixViewModel>();
        services.AddSingleton<ModCompatibilityService>();

        services.AddSingleton<IExternalToolService>(sp => new ExternalToolService(
            sp.GetRequiredService<IExternalToolRepository>(),
            sp.GetRequiredService<ILogger<ExternalToolService>>()));
        services.AddSingleton<IPluginService, PluginService>();

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IModRepository>(),
            sp.GetService<ILogger<SearchService>>()));

        // Task Center
        services.AddSingleton<TaskService>();
        services.AddSingleton<PlatformDownloadTaskRunner>();

        // Workers
        services.AddSingleton<IWorker, ImportWorker>();
        services.AddSingleton<IWorker, ScanWorker>();
        services.AddSingleton<IWorker, ValidationWorker>();

        // 部署相关（补充缺失的注册）
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<DeploymentStateMigrationService>();
        services.AddSingleton<ICrashHistoryRepository>(_ => new CrashHistoryRepository(dbPath));
        services.AddSingleton<CrashDeploymentHistoryService>();
        services.AddSingleton<IMinidumpAnalyzer>(sp => new MinidumpStackwalkAnalyzer(
            Path.Combine(AppContext.BaseDirectory, "Tools", "minidump-stackwalk", "minidump-stackwalk.exe"),
            storageSettings.Current.TempPath, sp.GetService<ILogger<MinidumpStackwalkAnalyzer>>()));
        services.AddSingleton<CrashDiagnosticsService>();

        // 注册三种部署方法
        services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();

        services.AddSingleton(sp => new DeploymentService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IDeploymentRepository>(),
            sp.GetServices<IDeploymentMethod>(),
            sp.GetService<ILogger<DeploymentService>>(),
            sp.GetRequiredService<CrashDeploymentHistoryService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<DeploymentStateMigrationService>()));

        // 新增核心服务
        services.AddSingleton(sp =>
        {
            var settings = new SettingsService(
            sp.GetRequiredService<ISettingsRepository>(),
            appDataPath,
            sp.GetService<ILogger<SettingsService>>());
            diagnosticLogSettings.Attach(settings);
            return settings;
        });

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
        services.AddSingleton<HD2Mimnt.Core.Interop.IOfflineInteropSource>(_ =>
            new HD2Mimnt.Infrastructure.Interop.OfflineInteropSnapshotReader(dbPath));
        services.AddSingleton(_ => new HD2Mimnt.Infrastructure.Interop.OfflineInteropPublisher(storageSettings.BootstrapPath));
        services.AddSingleton<OfflineInteropService>();
        services.AddSingleton<IFileLogService>(new FileLogService(appDataPath));

        services.AddSingleton(sp => new GameDetectionService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetService<ILogger<GameDetectionService>>()));

        services.AddSingleton<NotificationService>();
        services.AddSingleton<ConfirmationService>();
        services.AddSingleton<IConfirmationService>(sp => sp.GetRequiredService<ConfirmationService>());
        services.AddSingleton<ImagePreviewService>();

        services.AddSingleton<ModCompatibilityViewModel>();
        services.AddSingleton<IModelPreviewReader, HD2Mimnt.Infrastructure.ModelPreview.PatchResourceInspectionService>();
        services.AddSingleton<ModelPreviewService>();
        services.AddSingleton<ModelPreviewViewModel>();

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
            sp.GetRequiredService<IWebsiteDownloadLauncher>(),
            sp.GetRequiredService<BrowserDownloadHandoffService>(),
            sp.GetRequiredService<ModCompatibilityViewModel>(),
            sp.GetRequiredService<PlatformDownloadTaskRunner>(), sp.GetRequiredService<ModelPreviewViewModel>()));

        services.AddTransient(sp => new ProfileViewModel(
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IConfirmationService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ImagePreviewService>(),
            sp.GetRequiredService<DeploymentService>(),
            sp.GetRequiredService<ModCompatibilityViewModel>(), sp.GetRequiredService<ModelPreviewViewModel>()));

        services.AddTransient(sp => new ConflictsViewModel(
            sp.GetRequiredService<ConflictDetectionService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<NotificationService>()));

        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IImageCacheService>(),
            sp.GetRequiredService<DownloadSettingsService>(),
            sp.GetRequiredService<UiLocalization>(),
            sp.GetRequiredService<StorageSettingsService>(),
            sp.GetRequiredService<NexusModsAccountViewModel>(), sp.GetRequiredService<AyakaModsAccountViewModel>(),
            sp.GetRequiredService<IConfirmationService>(), sp.GetRequiredService<ModCompatibilityService>()));
        services.AddTransient(sp => new TaskCenterViewModel(
            sp.GetRequiredService<TaskService>(),
            sp.GetRequiredService<IConfirmationService>()));
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
            sp.GetRequiredService<IFileLogService>(), sp.GetRequiredService<SettingsService>()));
        services.AddTransient<CrashDiagnosticsViewModel>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASPackageReader, HD2Mimnt.Infrastructure.Hd2IAS.PackageManifestReader>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASRuntimeInspector, HD2Mimnt.Infrastructure.Hd2IAS.RuntimeCompatibilityChecker>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASLuaCompiler, HD2Mimnt.Infrastructure.Hd2IAS.LuaCompilerValidator>();
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("hd2ias-updates", (_, _) =>
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASUpdateSource>(sp =>
            new HD2Mimnt.Infrastructure.Hd2IAS.GitHubUpdateSource(
                sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("hd2ias-updates"),
                sp.GetRequiredService<IHttpDownloadService>()));
        services.AddSingleton(sp => new HD2Mimnt.Infrastructure.Hd2IAS.Hd2IASDeploymentStore(Path.Combine(storageLocations.AppDataPath, "HD2IAS"),
            Path.Combine(storageLocations.CachePath, "HD2IAS"), sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASPackageReader>()));
        services.AddSingleton(sp => new Hd2IASIntegrationService(
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASPackageReader>(),
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASRuntimeInspector>(),
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASLuaCompiler>(),
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASUpdateSource>(),
            Path.Combine(storageLocations.CachePath, "HD2IAS"),
            sp.GetRequiredService<HD2Mimnt.Infrastructure.Hd2IAS.Hd2IASDeploymentStore>(),
            sp.GetRequiredService<IDeploymentRepository>(), sp.GetRequiredService<ModService>()));
        services.AddTransient<Hd2IASManagementViewModel>();
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
            sp.GetRequiredService<ManagerMigrationViewModel>(),
            sp.GetRequiredService<BrowseModsViewModel>(),
            sp.GetRequiredService<CrashDiagnosticsViewModel>(),
            sp.GetRequiredService<DirectDownloadViewModel>(),
            sp.GetRequiredService<ModCreationViewModel>(),
            // HD2IAS 管理暂时停用：上游正在大改内测，旧管理页与自动检查不再接线。
            null,
            sp.GetRequiredService<ModCompatibilityViewModel>(), sp.GetRequiredService<SoundFixViewModel>()));
    }
}
