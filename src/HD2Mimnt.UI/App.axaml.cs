using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Scheduling;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Localization;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Storage;
using HD2Mimnt.Infrastructure.Migration;
using HD2Mimnt.Infrastructure.NativeCompanion;
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
            // UI 启动早期先让 CLI 的 Agent 守护进程退出，避免与 UI 并发写 SQLite 或产生内存状态分歧。
            await AgentHandoff.RequestAgentExitAsync();

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

            // 每次启动写一条会话横幅：日志里能明确切分本次运行与上次运行，
            // 版本/级别/系统走作用域字段，不占用正文。
            var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
            using (startupLogger.BeginScope(new Dictionary<string, object?>
                   {
                       ["version"] = HD2Mimnt.Core.AppInfo.Version,
                       ["level"] = host.Services.GetRequiredService<DiagnosticLogSettings>().MinimumLevel,
                       ["os"] = Environment.OSVersion.Version.ToString()
                   }))
            {
                startupLogger.LogInformation("===== 会话开始 =====");
            }

            // 版本一致性自检：更新可能只覆盖了部分文件（Worker/Agent 等子进程锁定 exe 被跳过），
            // 留下新旧混杂的安装目录，表现为部署等后台任务报 Method not found。
            // 启动时明确告警并落日志，避免用户面对难懂的运行期异常。
            try
            {
                CheckAssemblyVersionConsistency(startupLogger);
            }
            catch (Exception versionCheckError)
            {
                startupLogger.LogWarning(versionCheckError, "版本一致性自检失败");
            }

            // 完整性自检必须先于任何读库服务（SettingsService、本地化）构造：
            // 损坏时备份现场并尝试重建，否则后续每条读写都会以 SQLite Error 11 失败。
            var dbContext = ServiceProvider.GetRequiredService<HD2MimntDbContext>();
            var health = await DatabaseIntegrityGuard.EnsureHealthyAsync(dbContext.DatabasePath,
                ServiceProvider.GetService<ILogger<HD2MimntDbContext>>(), cancellation.Token);
            if (health is { Repaired: true, BackupDirectory: { } backupDirectory })
                ServiceProvider.GetRequiredService<NotificationService>().ShowWarning(
                    $"检测到数据库文件损坏，已自动修复（{health.Detail}）。原文件备份于：{backupDirectory}",
                    "数据库修复");

            desktop.ShutdownRequested += (_, _) =>
                host.Services.GetService<ILoggerFactory>()?.CreateLogger("App")
                    .LogInformation("===== 会话结束 =====");

            // 保留天数变更后立刻清理，不必等下一次启动或跨天切换。
            host.Services.GetRequiredService<SettingsService>().SettingChanged += (_, e) =>
            {
                if (e.Key != DiagnosticLogSettings.RetentionKey) return;
                host.Services.GetService<IFileLogService>()?.TrimExpired();
            };

            // 在 ViewModel/窗口构造前应用保存的语言，自动模式持续跟随系统。
            var localization = ServiceProvider.GetRequiredService<LocalizationService>();
            ServiceProvider.GetRequiredService<UiLocalization>();
            localization.StartMonitoring();
            desktop.Exit += (_, _) => localization.Dispose();

            // CPU 调度观察器只依赖 Application 服务；启动失败不影响主界面。
            var cpuScheduling = ServiceProvider.GetRequiredService<IGameCpuSchedulingService>();
            cpuScheduling.Start();
            desktop.ShutdownRequested += (_, _) =>
            {
                try { cpuScheduling.StopAsync().Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception ex)
                {
                    ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("CpuScheduling")
                        .LogWarning(ex, "CPU 调度停止时恢复原始设置失败");
                }
            };

            // 初始化数据库与 Worker；异步启动后不能在 UI 上同步等待数据库续体。
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
            foreach (var worker in ServiceProvider.GetServices<IWorker>())
            {
                taskService.RegisterWorker(worker);
            }

            // 任务历史此前只增不减：启动时清理 90 天前的终态记录，避免 Tasks 表与历史查询随使用无限增长。
            try
            {
                await taskService.CleanupOldTasksAsync(TimeSpan.FromDays(90), cancellation.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceProvider.GetService<ILogger<TaskService>>()
                    ?.LogWarning(ex, "启动清理旧任务记录失败");
            }

            // AI 旧明文密钥无条件迁移：懒迁移依赖打开 AI 设置页，从不打开则备份库仍带明文。
            try
            {
                var migratedKeys = await ServiceProvider.GetRequiredService<IAIProviderConfigRepository>()
                    .MigrateAllPlainKeysAsync(cancellation.Token);
                if (migratedKeys > 0)
                {
                    ServiceProvider.GetService<ILogger<AIProviderConfigRepository>>()
                        ?.LogInformation("已把 {Count} 条 AI 供应商明文密钥迁移到加密存储", migratedKeys);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceProvider.GetService<ILogger<AIProviderConfigRepository>>()
                    ?.LogWarning(ex, "AI 明文密钥迁移失败，将在下次启动重试");
            }

            // 进程被杀残留的导入/转换工作区按前缀清扫（超过一天的残留）
            await Task.Run(
                () => ModImportService.CleanupStaleWorkspaces(
                    ServiceProvider.GetRequiredService<StorageSettingsService>().Current.TempPath),
                cancellation.Token);

            // 上次进程异常退出会留下没有执行句柄的“运行中”记录，启动时先收尾，任务中心才不会出现取消不掉的僵尸任务。
            try
            {
                await taskService.RecoverInterruptedTasksAsync(cancellation.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                startupLogger.LogWarning(ex, "收尾上次中断的后台任务失败");
            }

            // CLI 管道宿主：允许外部 HD2Mimnt.Cli 在本进程内执行管理命令（仅主实例走到这里）。
            Program.CliPipeHost = new CliCommandPipeHost(host.Services);

            // 主题必须在窗口创建前应用：App.axaml 的默认深色会先渲染，等设置异步加载完再切换，
            // 浅色/皮肤主题用户会先看到一帧深色。SettingsService 构造时已同步载入设置，可直接读取。
            try
            {
                AppThemeService.Apply(ServiceProvider.GetRequiredService<SettingsService>()
                    .GetSetting(SettingKeys.Theme, AppThemeService.DefaultTheme));
            }
            catch (Exception ex)
            {
                // 换肤失败不能阻断启动：保留默认深色，用户仍可在设置页重新选择。
                startupLogger.LogWarning(ex, "启动期应用保存的主题失败，使用默认深色");
            }

            // 界面字体与主题一样必须在窗口创建前应用，避免先按默认字体渲染一帧。
            try
            {
                var fontSettings = ServiceProvider.GetRequiredService<SettingsService>();
                AppFontService.Apply(
                    fontSettings.GetSetting(SettingKeys.FontSource, AppFontService.SourceDefault),
                    fontSettings.GetSetting(SettingKeys.FontFamilyName, string.Empty),
                    fontSettings.GetSetting(SettingKeys.FontFilePath, string.Empty));
            }
            catch (Exception ex)
            {
                startupLogger.LogWarning(ex, "启动期应用保存的界面字体失败，使用默认字体");
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
            // 毛玻璃在窗口显示前应用：浅色主题不必先渲染一帧不透明背景再切换。
            var appearanceSettings = ServiceProvider.GetRequiredService<SettingsService>();
            UiMotion.Apply(appearanceSettings.GetSetting(SettingKeys.EnableAnimations, true));
            GlassMaterialService.Apply(
                mainWindow,
                ActualThemeVariant,
                appearanceSettings.GetSetting(SettingKeys.GlassIntensity, GlassMaterialService.DefaultIntensity),
                appearanceSettings.GetSetting(SettingKeys.GlassBlur, LiveBackdropService.DefaultBlur),
                !string.IsNullOrWhiteSpace(appearanceSettings.GetSetting(SettingKeys.BackgroundImagePath, string.Empty)),
                appearanceSettings.GetSetting(SettingKeys.CardOpacity, 100d),
                screenshotFriendly: !appearanceSettings.GetSetting(SettingKeys.GlassLiveBackdrop, false));
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

    /// <summary>扫描安装目录与 lib 的 HD2Mimnt 程序集版本，与主 exe 不一致即告警（安装目录新旧混杂的信号）。</summary>
    private static void CheckAssemblyVersionConsistency(ILogger startupLogger)
    {
        var mainVersion = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).FileVersion;
        if (string.IsNullOrEmpty(mainVersion))
            return;

        var libDirectory = Path.Combine(AppContext.BaseDirectory, "lib");
        IEnumerable<string> files = Directory.EnumerateFiles(AppContext.BaseDirectory, "HD2Mimnt.*");
        if (Directory.Exists(libDirectory))
            files = files.Concat(Directory.EnumerateFiles(libDirectory, "HD2Mimnt.*.dll"));

        var mismatched = files
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(f => !string.Equals(FileVersionInfo.GetVersionInfo(f).FileVersion, mainVersion, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        if (mismatched.Count == 0)
            return;

        var detail = string.Join(", ", mismatched);
        startupLogger.LogError("程序集版本混杂（期望 {Expected}）：{Files}", mainVersion, detail);
        ServiceProvider!.GetRequiredService<NotificationService>().ShowError(
            $"检测到程序文件版本不一致（{detail}），部署等后台任务可能报 Method not found。请重新安装或完整更新应用。",
            "版本混杂");
    }

    private static async Task InitializeMainWindowAsync(MainWindowViewModel main, MainWindow window)
    {
        // 窗口已显示后才执行初始化，任何失败必须可见，不能静默丢失。
        try
        {
            await main.InitializeAsync();
            var interop = ServiceProvider!.GetRequiredService<OfflineInteropService>();
            await interop.StartAsync();
            // 导入/部署/清理在子进程写入数据库，主进程的服务事件不会触发，任务结束后主动刷新离线快照。
            ServiceProvider!.GetRequiredService<TaskService>().TaskCompleted += (_, args) =>
            {
                if (args.Task.Type is "Import" or "Deployment" or "Cleanup") interop.RequestRefresh();
            };
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
        Directory.CreateDirectory(storageLocations.InstalledPluginsPath);
        services.AddSingleton(storageSettings);
        services.AddSingleton(new PluginWebView2Environment(Path.Combine(storageLocations.TempPath, "PluginWebView2")));

        var diagnosticLogSettings = new DiagnosticLogSettings();
        services.AddSingleton(diagnosticLogSettings);
        // 筛选只读内存快照，避免记录设置日志时重新获取设置锁。
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            // 按天分片 + 保留天数由 DiagnosticLogSettings 快照提供，避免写日志时读设置库。
            builder.AddProvider(new FileLogProvider(
                Path.Combine(appDataPath, "Logs"), "application", () => diagnosticLogSettings.RetentionDays));
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
        services.AddSingleton<IAIProviderConfigRepository, AIProviderConfigRepository>();
        services.AddSingleton<IAIService, AIService>();

        // 插件安全边界：密钥守卫、AI 中介、违规上报
        services.AddSingleton<IPluginSecretGuard, PluginSecretGuard>();
        services.AddSingleton<IPluginAiBroker, PluginAiBroker>();
        // 会话级授权：安装时选“仅本次允许”的权限只留在内存，重启失效
        services.AddSingleton<HD2Mimnt.Core.Interfaces.IPluginGrantSession, HD2Mimnt.Core.Interfaces.PluginGrantSession>();
        // 权限统一判定：PluginService 的权限检查与宿主启动快照共用
        services.AddSingleton<HD2Mimnt.Core.Interfaces.IPluginPermissionChecker, HD2Mimnt.Application.Services.PluginPermissionChecker>();
        services.AddSingleton<IPluginSecurityReporter, PluginSecurityReporter>();
        services.AddSingleton<IPluginHostManager>(sp => new PluginHostManager(
            permissionChecker: sp.GetRequiredService<HD2Mimnt.Core.Interfaces.IPluginPermissionChecker>()));
        services.AddSingleton<OptionTranslationService>();

        // 图片服务
        services.AddSingleton<IImageCacheService>(sp =>
            new ImageCacheService(imageCachePath, memoryCacheSize: 100));

        // Dedicated archive client: do not share platform API authentication headers.
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("downloads", (_, _) =>
        {
            // HttpClient.Timeout 是“整个响应周期”的总上限（含流式读取响应体），
            // 默认 100 秒会让慢速网络的自更新分段请求必然超时：改为无限总时长，
            // 由 HttpDownloadService 内部的响应头死线与读取空闲看门狗负责超时。
            var client = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30)
            })
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
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
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IDeploymentRepository>()));
        services.AddSingleton<ModBinarySearchService>();

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
        services.AddSingleton<ModGpuUsageService>();
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
        services.AddTransient<ModBinarySearchViewModel>();
        services.AddSingleton<ModCompatibilityService>();
        services.AddSingleton<ILuaExtractionStore, LuaExtractionRepository>();
        services.AddSingleton<LuaExtractionService>();

        services.AddSingleton<IExternalToolService>(sp => new ExternalToolService(
            sp.GetRequiredService<IExternalToolRepository>(),
            sp.GetRequiredService<ILogger<ExternalToolService>>()));
        services.AddSingleton<IPluginService>(sp => new PluginService(
            sp.GetRequiredService<IPluginRepository>(),
            sp.GetRequiredService<IPluginPermissionRepository>(),
            sp.GetRequiredService<IPluginExecutionRepository>(),
            sp.GetRequiredService<IPluginHostManager>(),
            permissionChecker: sp.GetRequiredService<HD2Mimnt.Core.Interfaces.IPluginPermissionChecker>(),
            installedPluginsPath: storageLocations.InstalledPluginsPath));

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IModRepository>(),
            sp.GetService<ILogger<SearchService>>()));

        // Task Center
        services.AddSingleton<IWorkerHostManager>(sp => new WorkerHostManager(
            sp.GetService<ILogger<WorkerHostManager>>()));
        services.AddSingleton(sp =>
        {
            var taskService = new TaskService(
                sp.GetRequiredService<ITaskRepository>(),
                sp,
                sp.GetRequiredService<IWorkerHostManager>());
            // 重 IO 任务在 HD2Mimnt.Worker 独立进程执行，崩溃/卡死不影响主 UI。
            // 注册放在 DI 工厂里，UI 测试只构建服务容器也能拿到完整任务图。
            taskService.RegisterProcessWorker("Validation");
            taskService.RegisterProcessWorker("Deployment");
            taskService.RegisterProcessWorker("Cleanup");
            taskService.RegisterProcessWorker("HellForgeSync");
            taskService.RegisterProcessWorker("Import");
            taskService.RegisterProcessWorker("Hd2ClConversion");
            taskService.RegisterProcessWorker("ModelToIasConversion");
            taskService.RegisterProcessWorker("MmdAdaptation");
            return taskService;
        });
        services.AddSingleton<PlatformDownloadTaskRunner>();

        // 部署相关（补充缺失的注册）
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<DeploymentStateMigrationService>();

        // 注册三种部署方法
        services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();

        // MDL3 原生伴生组件安装（签名校验；3.0+ 落位 %ProgramData%\MDL3，2.x 落位 %LOCALAPPDATA%\MDL3）。
        services.AddSingleton(sp => new Mdl3CompanionService(
            sp.GetService<IAuthenticodeVerifier>() ?? new AuthenticodeVerifier(),
            sp.GetService<ILogger<Mdl3CompanionService>>()));

        services.AddSingleton(sp => new DeploymentService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IDeploymentRepository>(),
            sp.GetServices<IDeploymentMethod>(),
            sp.GetService<ILogger<DeploymentService>>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<DeploymentStateMigrationService>(),
            // IAS armor 配套生成在部署成功后触发；延迟解析避免 ModService↔DeploymentService 构造环。
            () => sp.GetService<HD2Mimnt.Core.Interfaces.IIasCompanionService>(),
            sp.GetRequiredService<Mdl3CompanionService>()));

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
        services.AddSingleton<IFileLogService>(new FileLogService(
            Path.Combine(appDataPath, "Logs"), () => diagnosticLogSettings.RetentionDays));

        services.AddSingleton(sp => new GameDetectionService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<HellForgeSessionService>(),
            sp.GetService<ILogger<GameDetectionService>>()));

        // 游戏运行时 CPU 调度：拓扑/控制器/观察器都在 Infrastructure，UI 只经过 Application 服务。
        services.AddSingleton<ICpuTopologyProvider, Win32CpuTopologyProvider>();
        services.AddSingleton<IProcessSchedulingNativeApi, Win32ProcessSchedulingNativeApi>();
        services.AddSingleton<IProcessSchedulingController>(sp => new Win32ProcessSchedulingController(
            sp.GetRequiredService<IProcessSchedulingNativeApi>()));
        services.AddSingleton<IGameProcessLocator, GameProcessLocator>();
        services.AddSingleton<IGameCpuSchedulingService>(sp => new GameCpuSchedulingService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ICpuTopologyProvider>(),
            sp.GetRequiredService<IProcessSchedulingController>(),
            sp.GetRequiredService<IGameProcessLocator>(),
            sp.GetService<ILogger<GameCpuSchedulingService>>()));
        services.AddSingleton(sp => new HellForgeService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetService<ILogger<HellForgeService>>()));
        services.AddSingleton(sp => new HellForgeSessionService(
            sp.GetRequiredService<HellForgeService>(),
            sp.GetService<ILogger<HellForgeSessionService>>()));
        services.AddSingleton(sp => new HellForgeModSyncService(
            sp.GetRequiredService<HellForgeService>(),
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetServices<IDeploymentMethod>(),
            Path.Combine(appDataPath, "hellforge-sync.json"),
            sp.GetService<ILogger<HellForgeModSyncService>>(),
            Path.Combine(storageLocations.CachePath, "HellForgeSync")));

        services.AddSingleton<NotificationService>();
        services.AddSingleton<ConfirmationService>();
        services.AddSingleton<IConfirmationService>(sp => sp.GetRequiredService<ConfirmationService>());
        services.AddSingleton<Hd2ClVariantPromptService>();
        services.AddSingleton<IHd2ClVariantPrompt>(sp => sp.GetRequiredService<Hd2ClVariantPromptService>());
        services.AddSingleton<MmdSourceInfoPromptService>();
        services.AddSingleton<IHd2ClMmdSourceInfoPrompt>(sp => sp.GetRequiredService<MmdSourceInfoPromptService>());
        services.AddSingleton<ImagePreviewService>();

        services.AddSingleton<ModCompatibilityViewModel>();
        // 预览解析/解码/音频播放都放到独立 Worker 进程：关闭预览即终止进程，内存立即归还。
        services.AddSingleton<PreviewWorkerSession>();
        services.AddSingleton<IPreviewWorkerSession>(sp =>
            sp.GetRequiredService<PreviewWorkerSession>());
        services.AddSingleton<IModelPreviewReader>(sp =>
            new RemoteModelPreviewReader(sp.GetRequiredService<PreviewWorkerSession>()));
        services.AddSingleton<ModelPreviewService>();
        services.AddSingleton<ModelPreviewViewModel>();

        services.AddSingleton<IAudioPreviewReader>(sp =>
            new RemoteAudioPreviewReader(sp.GetRequiredService<PreviewWorkerSession>()));
        services.AddSingleton<IAudioPlaybackEngine>(sp =>
            new RemoteAudioPlaybackEngine(sp.GetRequiredService<PreviewWorkerSession>()));
        services.AddSingleton<HD2Mimnt.Infrastructure.AudioPreview.GameAudioBaselineProvider>();
        services.AddSingleton<AudioPreviewService>();
        services.AddSingleton<AudioPreviewViewModel>();

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
            sp.GetRequiredService<PlatformDownloadTaskRunner>(), sp.GetRequiredService<ModelPreviewViewModel>(), sp.GetRequiredService<AudioPreviewViewModel>(),
            sp.GetRequiredService<LuaExtractionService>(), sp.GetRequiredService<IPluginService>(),
            sp.GetRequiredService<TaskService>(), sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2ClConversionService>(),
            sp.GetRequiredService<IHd2ClVariantPrompt>(), sp.GetRequiredService<ModGpuUsageService>(),
            sp.GetRequiredService<HellForgeModSyncService>()));

        services.AddTransient(sp => new ProfileViewModel(
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IConfirmationService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ImagePreviewService>(),
            sp.GetRequiredService<DeploymentService>(),
            sp.GetRequiredService<ModCompatibilityViewModel>(), sp.GetRequiredService<ModelPreviewViewModel>(), sp.GetRequiredService<AudioPreviewViewModel>(),
            sp.GetRequiredService<OptionTranslationService>(),
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2ClConversionService>(),
            sp.GetRequiredService<IHd2ClVariantPrompt>(), sp.GetRequiredService<ModGpuUsageService>()));

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
            sp.GetRequiredService<IConfirmationService>(), sp.GetRequiredService<ModCompatibilityService>(),
            sp.GetRequiredService<AiTranslationSettingsViewModel>(),
            sp.GetRequiredService<AppUpdateViewModel>(), sp.GetRequiredService<IBrowserLauncher>(),
            sp.GetRequiredService<IGameCpuSchedulingService>()));
        services.AddTransient(sp => new TaskCenterViewModel(
            sp.GetRequiredService<TaskService>(),
            sp.GetRequiredService<IConfirmationService>()));
        services.AddTransient(sp => new ExternalToolsViewModel(
            sp.GetRequiredService<IExternalToolService>(),
            sp.GetRequiredService<ConfirmationService>()));
        services.AddTransient(sp => new PluginManagementViewModel(
            sp.GetRequiredService<IPluginService>(),
            sp.GetRequiredService<ConfirmationService>()));
        services.AddTransient(sp => new AiTranslationSettingsViewModel(
            sp.GetRequiredService<IAIService>(),
            sp.GetRequiredService<IAIProviderConfigRepository>(),
            sp.GetRequiredService<SettingsService>()));
        services.AddTransient(sp => new BackupViewModel(
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<ConfirmationService>(),
            sp.GetRequiredService<NotificationService>()));
        services.AddTransient(sp => new LogViewModel(
            sp.GetRequiredService<IFileLogService>(), sp.GetRequiredService<SettingsService>()));
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
        // 新版 IAS 系统（HD2IntegratedArmorSystem）：位置/Runtime 发现 + armor 配套生成。
        services.AddSingleton<HD2Mimnt.Core.Interfaces.IIasSystemService>(sp =>
            new IasSystemService(sp.GetRequiredService<ModService>(), sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<HD2Mimnt.Core.Interfaces.IIasCompanionService>(sp =>
            new IasCompanionService(
                sp.GetRequiredService<HD2Mimnt.Core.Interfaces.IIasSystemService>(),
                sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<HD2Mimnt.Core.Interfaces.IDeploymentRepository>(),
                sp.GetRequiredService<SettingsService>(),
                Path.Combine(appDataPath, "IAS-Transactions"),
                Path.Combine(appDataPath, "Cache", "IAS-Generator"),
                Path.Combine(appDataPath, "IAS-System", "deployment.json")));
        services.AddTransient<IasSystemViewModel>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2ClConversionService>(sp =>
            new Hd2ClConversionService(sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<TaskService>()));
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IModelToIasConversionService>(sp =>
            new ModelToIasConversionService(sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<TaskService>(), sp.GetRequiredService<StorageSettingsService>()));
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IMmdAdaptationService>(sp =>
            new MmdAdaptationService(sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<TaskService>()));
        services.AddTransient<Hd2ClViewModel>();
        services.AddTransient<ModelToIasViewModel>();
        services.AddTransient(sp => new MmdAdaptationViewModel(
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IMmdAdaptationService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IHd2ClMmdSourceInfoPrompt>()));
        services.AddTransient<HellForgeViewModel>();
        services.AddTransient(sp => new NotificationPanelViewModel(
            sp.GetRequiredService<NotificationService>()));

        // 应用自更新：检查/下载/安装编排 + "关于"页视图模型
        services.AddKeyedSingleton<System.Net.Http.HttpClient>("app-update", (_, _) => new System.Net.Http.HttpClient());
        services.AddSingleton<HD2Mimnt.Core.Interfaces.IAppUpdateService>(sp =>
            new HD2Mimnt.Application.Services.AppUpdateService(
                new HD2Mimnt.Infrastructure.Updates.GitHubAppUpdateClient(
                    sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("app-update")),
                sp.GetRequiredService<IHttpDownloadService>(),
                sp.GetRequiredService<StorageSettingsService>(),
                logger: sp.GetRequiredService<ILogger<HD2Mimnt.Application.Services.AppUpdateService>>(),
                fallbackReleaseClient: new HD2Mimnt.Infrastructure.Updates.CodebergAppUpdateClient(
                    sp.GetRequiredKeyedService<System.Net.Http.HttpClient>("app-update")),
                settings: sp.GetRequiredService<SettingsService>(),
                downloadOptionsProvider: sp.GetRequiredService<IDownloadOptionsProvider>()));
        services.AddSingleton<AppUpdateViewModel>();

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
            sp.GetRequiredService<DeploymentService>(),
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
            sp.GetRequiredService<DirectDownloadViewModel>(),
            sp.GetRequiredService<ModCreationViewModel>(),
            // HD2IAS 管理：上游仍在内测，旧管理页重新开放入口，页内各区块默认折叠。
            sp.GetRequiredService<Hd2IASManagementViewModel>(),
            sp.GetRequiredService<ModCompatibilityViewModel>(), sp.GetRequiredService<SoundFixViewModel>(),
            sp.GetRequiredService<IasSystemViewModel>(),
            sp.GetRequiredService<AppUpdateViewModel>(),
            sp.GetRequiredService<IPluginService>(),
            sp.GetRequiredService<HellForgeViewModel>(),
            sp.GetRequiredService<HellForgeSessionService>(),
            sp.GetRequiredService<Hd2ClViewModel>(),
            sp.GetRequiredService<ModelToIasViewModel>(),
            sp.GetRequiredService<Hd2ClVariantPromptService>(),
            sp.GetRequiredService<MmdAdaptationViewModel>(),
            sp.GetRequiredService<MmdSourceInfoPromptService>(),
            sp.GetRequiredService<ModBinarySearchViewModel>(),
            sp.GetRequiredService<PluginWebView2Environment>()));
    }
}
