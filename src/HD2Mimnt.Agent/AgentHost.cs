using System.Net.Http;
using System.Text.Json;
using HD2Mimnt.Application.Cli;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.Interop;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Infrastructure.Interop;
using HD2Mimnt.Infrastructure.Localization;
using HD2Mimnt.Infrastructure.Migration;
using HD2Mimnt.Infrastructure.NativeCompanion;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Scheduling;
using HD2Mimnt.Infrastructure.SoundFix;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Agent;

/// <summary>
/// Agent 服务宿主：按与 UI 相同的存储路径构建完整业务服务图（无 Avalonia/ViewModel）。
/// serve 模式常驻持有容器；run-once 模式构建后执行单条命令即退出。
/// 启动序列与 App.axaml.cs 对齐：存储迁移 → 完整性守卫 → 建库 → 部署状态收养 → 配置兜底 → 任务恢复。
/// </summary>
internal static class AgentHost
{
    private static readonly JsonSerializerOptions PacketOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>构建完整容器并完成启动序列；serve 与 run-once 共用。</summary>
    public static async Task<ServiceProvider> BuildAsync(CancellationToken cancellationToken)
    {
        var storageSettings = new StorageSettingsService(new StorageLocationStore());
        AgentLog.Initialize(storageSettings.Current.AppDataPath);
        // 与 UI 启动顺序一致：先完成上次提交的存储迁移，再按最终目录创建服务。
        await storageSettings.InitializeAsync(cancellationToken);
        AgentLog.Initialize(storageSettings.Current.AppDataPath);

        var services = new ServiceCollection();
        ConfigureServices(services, storageSettings);
        var provider = services.BuildServiceProvider();

        var dbContext = provider.GetRequiredService<HD2MimntDbContext>();
        // 允许完整修复；损坏时先备份再重建，避免服务期每条命令都失败。
        var health = await DatabaseIntegrityGuard.EnsureHealthyAsync(dbContext.DatabasePath,
            provider.GetService<ILogger<HD2MimntDbContext>>(), cancellationToken);
        if (health is { Repaired: true, BackupDirectory: { } backupDirectory })
            AgentLog.Write($"检测到数据库文件损坏，已自动修复（{health.Detail}）。原文件备份于：{backupDirectory}");
        await dbContext.InitializeDatabaseAsync(cancellationToken);

        var settings = provider.GetRequiredService<SettingsService>();
        try
        {
            await provider.GetRequiredService<DeploymentStateMigrationService>()
                .EnsureMigratedAsync(settings.GetEffectiveGamePath(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write("部署状态迁移失败，继续使用原记录", ex);
        }

        await provider.GetRequiredService<ProfileService>().InitializeAsync(cancellationToken);

        var taskService = provider.GetRequiredService<TaskService>();
        try
        {
            await taskService.CleanupOldTasksAsync(TimeSpan.FromDays(90), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write("启动清理旧任务记录失败", ex);
        }

        try
        {
            var repository = provider.GetService<IAIProviderConfigRepository>();
            if (repository is not null)
            {
                var migratedKeys = await repository.MigrateAllPlainKeysAsync(cancellationToken);
                if (migratedKeys > 0) AgentLog.Write($"已迁移 {migratedKeys} 条 AI 供应商明文密钥");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write("AI 明文密钥迁移失败，将在下次启动重试", ex);
        }

        await Task.Run(() => ModImportService.CleanupStaleWorkspaces(storageSettings.Current.TempPath), cancellationToken);

        try
        {
            await taskService.RecoverInterruptedTasksAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write("收尾上次中断的后台任务失败", ex);
        }

        // 与 GUI 主进程对齐：Worker 子进程写库不触发主进程事件，任务终态后刷新离线互操作快照。
        var offlineInterop = provider.GetRequiredService<OfflineInteropService>();
        taskService.TaskCompleted += (_, _) => offlineInterop.RequestRefresh();

        return provider;
    }

    private static void ConfigureServices(IServiceCollection services, StorageSettingsService storageSettings)
    {
        var locations = storageSettings.Current;
        var appDataPath = locations.AppDataPath;
        Directory.CreateDirectory(appDataPath);
        // 加载器识别配置：应用目录 Config/LoaderCatalog.json 默认 + <AppData>/loaders.json 覆盖。
        LoaderRegistry.Initialize(appDataPath);
        Directory.CreateDirectory(locations.ModsPath);
        Directory.CreateDirectory(locations.ImageCachePath);
        Directory.CreateDirectory(locations.DownloadsPath);
        Directory.CreateDirectory(locations.TempPath);
        Directory.CreateDirectory(locations.InstalledPluginsPath);

        services.AddSingleton(storageSettings);
        // 无控制台日志提供程序：命令结果统一走 stdout/管道，诊断走 AgentLog。
        services.AddLogging();

        // 基础设施层
        services.AddSingleton(sp => new HD2MimntDbContext(locations.DatabasePath));
        services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(locations.DatabasePath));
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

        // 插件安全边界：密钥守卫、AI 中介、会话授权、权限判定、违规上报、宿主管理
        services.AddSingleton<IPluginSecretGuard, PluginSecretGuard>();
        services.AddSingleton<IPluginAiBroker, PluginAiBroker>();
        services.AddSingleton<IPluginGrantSession, PluginGrantSession>();
        services.AddSingleton<IPluginPermissionChecker, PluginPermissionChecker>();
        services.AddSingleton<IPluginSecurityReporter, PluginSecurityReporter>();
        services.AddSingleton<IPluginHostManager>(sp => new PluginHostManager(
            permissionChecker: sp.GetRequiredService<IPluginPermissionChecker>()));

        // 下载基础设施：专用下载客户端不共享平台 API 认证头
        services.AddKeyedSingleton<HttpClient>("downloads", (_, _) =>
        {
            // 总时长无限：慢速网络的自更新分段请求不能被 HttpClient.Timeout 掐断，
            // 超时由 HttpDownloadService 内部的响应头死线与读取空闲看门狗负责。
            var client = new HttpClient(new System.Net.Http.SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HD2Mimnt-ModManager");
            return client;
        });
        services.AddSingleton<DownloadSettingsService>();
        services.AddSingleton<IDownloadOptionsProvider>(sp => sp.GetRequiredService<DownloadSettingsService>());
        services.AddSingleton<IHttpDownloadService>(sp => new HttpDownloadService(
            sp.GetRequiredKeyedService<HttpClient>("downloads"),
            sp.GetRequiredService<IDownloadOptionsProvider>(), locations.DownloadsPath, locations.TempPath));
        services.AddSingleton<IDirectDownloadClientFactory>(sp => new DirectDownloadClientFactory(
            sp.GetRequiredService<IDownloadOptionsProvider>(), locations.DownloadsPath, locations.TempPath));
        services.AddSingleton(sp => new DirectDownloadService(sp.GetRequiredService<IDirectDownloadClientFactory>(),
            sp.GetRequiredService<IDownloadOptionsProvider>(), sp.GetRequiredService<SettingsService>(), locations.TempPath));

        // 外部平台：Nexus / GitHub / Ayaka
        services.AddKeyedSingleton<HttpClient>("nexus-api", (_, _) => new HttpClient());
        services.AddKeyedSingleton<HttpClient>("github-api", (_, _) => new HttpClient());
        services.AddKeyedSingleton<HttpClient>("ayaka-api", (_, _) =>
            new HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton(sp => new HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter(
            sp.GetRequiredKeyedService<HttpClient>("ayaka-api"), sp.GetRequiredService<IDownloadOptionsProvider>(),
            locations.DownloadsPath, locations.TempPath,
            logger: sp.GetRequiredService<ILogger<HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter>>()));
        services.AddSingleton<AyakaModsAuthService>();
        services.AddSingleton(new AyakaModsOAuthOptions
        { ClientId = Environment.GetEnvironmentVariable("HD2MIMNT_AYAKA_CLIENT_ID")?.Trim() ?? "" });
        services.AddKeyedSingleton<HttpClient>("ayaka-oauth", (_, _) =>
            new HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IAyakaModsOAuthClient>(sp => new HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsOAuthClient(
            sp.GetRequiredKeyedService<HttpClient>("ayaka-oauth")));
        services.AddSingleton<AyakaModsOAuthService>();
        services.AddSingleton(sp => new NexusModsPlatformAdapter(
            sp.GetRequiredKeyedService<HttpClient>("nexus-api"),
            sp.GetRequiredService<IHttpDownloadService>(), typeof(AgentHost).Assembly.GetName().Version!.ToString(3),
            sp.GetRequiredService<ILogger<NexusModsPlatformAdapter>>()));
        services.AddSingleton(sp => new GitHubPlatformAdapter(
            sp.GetRequiredKeyedService<HttpClient>("github-api"), sp.GetRequiredService<IHttpDownloadService>()));
        services.AddSingleton<IPlatformRegistry>(sp =>
        {
            var registry = new PlatformRegistry();
            registry.RegisterPlatform(sp.GetRequiredService<NexusModsPlatformAdapter>());
            registry.RegisterPlatform(sp.GetRequiredService<GitHubPlatformAdapter>());
            registry.RegisterPlatform(sp.GetRequiredService<HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter>());
            return registry;
        });
        services.AddSingleton<DownloadManagerService>();
        // OAuth 配置与 UI 的 NexusModsOAuthConfiguration.Create 同源（环境变量优先，获批后可填常量）。
        services.AddSingleton(new NexusModsOAuthOptions
        {
            ClientId = Environment.GetEnvironmentVariable("HD2MIMNT_NEXUS_CLIENT_ID") ?? "",
            RedirectUri = Environment.GetEnvironmentVariable("HD2MIMNT_NEXUS_REDIRECT_URI") ?? NexusModsOAuthOptions.DefaultRedirectUri
        });
        services.AddKeyedSingleton<HttpClient>("nexus-oauth", (_, _) =>
            new HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<INexusModsOAuthClient>(sp =>
            new HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsOAuthClient(
                sp.GetRequiredKeyedService<HttpClient>("nexus-oauth"),
                applicationVersion: typeof(AgentHost).Assembly.GetName().Version!.ToString(3)));
        services.AddSingleton<NexusModsTestService>();
        services.AddSingleton<NexusModsAuthService>(sp => sp.GetRequiredService<NexusModsTestService>());
        services.AddSingleton<ModLinkService>();
        services.AddSingleton<ModOnlineService>();
        services.AddSingleton(_ => new BrowserDownloadHandoffService(HD2Mimnt.Infrastructure.BrowserDownloadsDirectory.GetPath()));
        services.AddKeyedSingleton<HttpClient>("browse-images", (_, _) => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        services.AddSingleton(sp => new ModBrowseService(sp.GetRequiredService<IPlatformRegistry>(),
            sp.GetRequiredService<DownloadManagerService>(), sp.GetRequiredKeyedService<HttpClient>("browse-images"), locations.TempPath));
        services.AddSingleton<ModCreationService>();

        // 应用层业务服务
        services.AddSingleton(sp => new ModService(
            sp.GetRequiredService<IModRepository>(),
            locations.ModsPath,
            sp.GetService<ILogger<ModService>>(),
            sp.GetRequiredService<DeploymentService>(), locations.TempPath,
            sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<ProfileService>()));
        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IDeploymentRepository>()));

        services.AddSingleton(sp => new SettingsService(
            sp.GetRequiredService<ISettingsRepository>(),
            appDataPath,
            sp.GetService<ILogger<SettingsService>>()));

        // 外部管理器迁移：读取器只读源数据，实际复制和落库由 Application 服务完成。
        services.AddSingleton<IExternalManagerReader, Helldivers2ModManagerReader>();
        services.AddSingleton<IExternalManagerReader, EchelonReader>();
        services.AddSingleton<IExternalManagerReader, Hd2ArsenalReader>();
        services.AddSingleton<IExternalManagerWriter, Helldivers2ModManagerWriter>();
        services.AddSingleton<IExternalManagerWriter, EchelonWriter>();
        services.AddSingleton<IExternalManagerWriter, Hd2ArsenalWriter>();
        services.AddSingleton<ExternalManagerExportService>();
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
        services.AddKeyedSingleton<HttpClient>("compatibility-names", (_, _) => new HttpClient
        { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<ICompatibilityResourceNames>(sp => new CompatibilityResourceNameProvider(
            sp.GetRequiredKeyedService<HttpClient>("compatibility-names"), locations.TempPath));
        services.AddSingleton<IGameUnitReferenceReader>(_ => new GameUnitReferenceReader(locations.CachePath));
        services.AddSingleton<IGameSoundBankReader>(sp => (GameUnitReferenceReader)sp.GetRequiredService<IGameUnitReferenceReader>());
        services.AddSingleton<SoundFixInspector>();
        services.AddSingleton(sp => new SoundFixService(sp.GetRequiredService<IModRepository>(), sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<SoundFixInspector>(), sp.GetRequiredService<SettingsService>(),
            Path.Combine(appDataPath, "SoundFixBackups")));
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
            permissionChecker: sp.GetRequiredService<IPluginPermissionChecker>(),
            installedPluginsPath: locations.InstalledPluginsPath));

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IModRepository>(),
            sp.GetService<ILogger<SearchService>>()));

        // 任务中心与 Worker 进程宿主：重 IO 任务与 GUI 同链路（HD2Mimnt.Worker 独立进程）。
        services.AddSingleton<IWorkerHostManager>(sp => new WorkerHostManager(
            sp.GetService<ILogger<WorkerHostManager>>()));
        services.AddSingleton(sp =>
        {
            var taskService = new TaskService(
                sp.GetRequiredService<ITaskRepository>(),
                sp,
                sp.GetRequiredService<IWorkerHostManager>());
            taskService.RegisterProcessWorker("Validation");
            taskService.RegisterProcessWorker("Deployment");
            taskService.RegisterProcessWorker("Cleanup");
            taskService.RegisterProcessWorker("HellForgeSync");
            taskService.RegisterProcessWorker("Import");
            taskService.RegisterProcessWorker("Hd2ClConversion");
            taskService.RegisterProcessWorker("MmdAdaptation");
            return taskService;
        });

        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<DeploymentStateMigrationService>();
        services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();
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
            () => sp.GetService<IIasCompanionService>(),
            sp.GetRequiredService<Mdl3CompanionService>()));

        services.AddSingleton(sp => new BackupService(
            appDataPath,
            sp.GetRequiredService<HD2MimntDbContext>(),
            sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IOfflineInteropSource>(_ => new OfflineInteropSnapshotReader(locations.DatabasePath));
        services.AddSingleton(_ => new OfflineInteropPublisher(storageSettings.BootstrapPath));
        services.AddSingleton<OfflineInteropService>();

        services.AddSingleton(sp => new GameDetectionService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<HellForgeSessionService>(),
            sp.GetService<ILogger<GameDetectionService>>()));
        services.AddSingleton(sp => new HellForgeService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetService<ILogger<HellForgeService>>()));
        services.AddSingleton(sp => new HellForgeSessionService(
            sp.GetRequiredService<HellForgeService>(),
            sp.GetService<ILogger<HellForgeSessionService>>()));

        // CPU 调度：CLI 与 GUI 共用同一套服务图，命令与界面读取同一份状态。
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
        services.AddSingleton(sp => new HellForgeModSyncService(
            sp.GetRequiredService<HellForgeService>(),
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetServices<IDeploymentMethod>(),
            Path.Combine(appDataPath, "hellforge-sync.json"),
            sp.GetService<ILogger<HellForgeModSyncService>>(),
            Path.Combine(locations.CachePath, "HellForgeSync")));

        // NotificationService 无 UI 依赖（纯事件）；Agent 无订阅者，等价静音。
        services.AddSingleton<NotificationService>();

        services.AddSingleton<ISystemLanguageProvider, SystemLanguageProvider>();
        services.AddSingleton<TranslationCatalog>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton<OptionTranslationService>();

        // HD2IAS 管理：包读取三件套 + 更新源 + 部署存储 + 集成服务。
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASPackageReader, HD2Mimnt.Infrastructure.Hd2IAS.PackageManifestReader>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASRuntimeInspector, HD2Mimnt.Infrastructure.Hd2IAS.RuntimeCompatibilityChecker>();
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASLuaCompiler, HD2Mimnt.Infrastructure.Hd2IAS.LuaCompilerValidator>();
        services.AddKeyedSingleton<HttpClient>("hd2ias-updates", (_, _) => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<HD2Mimnt.Core.Hd2IAS.IHd2IASUpdateSource>(sp =>
            new HD2Mimnt.Infrastructure.Hd2IAS.GitHubUpdateSource(
                sp.GetRequiredKeyedService<HttpClient>("hd2ias-updates"),
                sp.GetRequiredService<IHttpDownloadService>()));
        services.AddSingleton(sp => new HD2Mimnt.Infrastructure.Hd2IAS.Hd2IASDeploymentStore(
            Path.Combine(appDataPath, "HD2IAS"),
            Path.Combine(locations.CachePath, "HD2IAS"),
            sp.GetRequiredService<HD2Mimnt.Core.Hd2IAS.IHd2IASPackageReader>()));
        services.AddSingleton(sp => new HD2Mimnt.Application.Services.Hd2IASIntegrationService(
            sp.GetRequiredService<IHd2IASPackageReader>(),
            sp.GetRequiredService<IHd2IASRuntimeInspector>(),
            sp.GetRequiredService<IHd2IASLuaCompiler>(),
            sp.GetRequiredService<IHd2IASUpdateSource>(),
            Path.Combine(locations.CachePath, "HD2IAS"),
            sp.GetRequiredService<HD2Mimnt.Infrastructure.Hd2IAS.Hd2IASDeploymentStore>(),
            sp.GetRequiredService<IDeploymentRepository>(), sp.GetRequiredService<ModService>()));

        // 新版 IAS 系统（HD2IntegratedArmorSystem）与 HD2CL/MMD 转换。
        services.AddSingleton<IIasSystemService>(sp =>
            new IasSystemService(sp.GetRequiredService<ModService>(), sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IIasCompanionService>(sp =>
            new IasCompanionService(
                sp.GetRequiredService<IIasSystemService>(),
                sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<IDeploymentRepository>(),
                sp.GetRequiredService<SettingsService>(),
                Path.Combine(appDataPath, "IAS-Transactions"),
                Path.Combine(appDataPath, "Cache", "IAS-Generator"),
                Path.Combine(appDataPath, "IAS-System", "deployment.json")));
        services.AddSingleton<IHd2ClConversionService>(sp =>
            new Hd2ClConversionService(sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<TaskService>()));
        services.AddSingleton<IMmdAdaptationService>(sp =>
            new MmdAdaptationService(sp.GetRequiredService<ModService>(),
                sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<TaskService>()));

        // 应用自更新。
        services.AddKeyedSingleton<HttpClient>("app-update", (_, _) => new HttpClient());
        services.AddSingleton<IAppUpdateService>(sp =>
            new AppUpdateService(
                new HD2Mimnt.Infrastructure.Updates.GitHubAppUpdateClient(
                    sp.GetRequiredKeyedService<HttpClient>("app-update")),
                sp.GetRequiredService<IHttpDownloadService>(),
                sp.GetRequiredService<StorageSettingsService>(),
                logger: sp.GetRequiredService<ILogger<AppUpdateService>>(),
                fallbackReleaseClient: new HD2Mimnt.Infrastructure.Updates.CodebergAppUpdateClient(
                    sp.GetRequiredKeyedService<HttpClient>("app-update")),
                settings: sp.GetRequiredService<SettingsService>()));
    }

    /// <summary>run-once：在容器内执行单条命令并返回退出码；进度直接写入控制台输出。</summary>
    public static async Task<int> RunOnceAsync(IReadOnlyList<string> tokens, bool json, bool yes, bool stream, bool async, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var provider = await BuildAsync(cancellationToken);
        var session = new CliCommandSession { Progress = new ConsoleProgressReporter(json, stream) };
        var response = await CliCommandCatalog.Build()
            .DispatchAsync(tokens, provider, json, yes, cancellationToken, session, async).ConfigureAwait(false);
        return Print(json, response);
    }

    private static int Print(bool json, CliCommandResponse response)
    {
        if (json)
        {
            var payload = JsonSerializer.Serialize(new CliPipeResult(response.ExitCode, response.Message, response.Data, response.RequiresYes), PacketOptions);
            Console.WriteLine(payload);
        }
        else
        {
            if (response.ExitCode == 2) Console.Error.WriteLine(response.Message);
            else Console.WriteLine(response.Message);
        }
        return response.ExitCode;
    }

    /// <summary>run-once 的进度直接写控制台：JSON+stream 走 stdout（NDJSON），其余写 stderr 不污染管道消费。</summary>
    private sealed class ConsoleProgressReporter(bool json, bool stream) : IProgress<CliProgressEvent>
    {
        public void Report(CliProgressEvent value)
        {
            if (!stream) return;
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new CliPipeProgress(value.Stage, value.Percent, value.Current, value.Message ?? ""), PacketOptions));
            }
            else
            {
                Console.Error.WriteLine($"[{value.Stage}]{(value.Percent is { } p ? $" {p:P0}" : "")} {value.Current ?? value.Message}");
            }
        }
    }
}
