using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.AudioPreview;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.ModelPreview;
using HD2Mimnt.Infrastructure.NativeCompanion;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Storage;
using HD2Mimnt.Infrastructure.Workflows;
using HD2Mimnt.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Worker 进程入口：在独立进程执行后台任务，崩溃或被强杀不影响主程序 UI。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Guid taskId = Guid.Empty;
        string pipeName = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--task-id" && i + 1 < args.Length)
            {
                Guid.TryParse(args[i + 1], out taskId);
            }
            else if (args[i] == "--pipe-name" && i + 1 < args.Length)
            {
                pipeName = args[i + 1];
            }
        }

        if (taskId == Guid.Empty || string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine("Usage: HD2Mimnt.Worker.exe --task-id <guid> --pipe-name <name>");
            return 1;
        }

        using var host = new WorkerHost(taskId, pipeName);
        try
        {
            await host.ConnectAsync(timeoutMilliseconds: 5000);
            var command = await host.ReceiveCommandAsync();
            if (command == null)
            {
                await host.SendErrorAsync(new InvalidOperationException("Failed to receive execute command"));
                return 1;
            }

            var services = new ServiceCollection();
            ConfigureServices(services);
            await using var serviceProvider = services.BuildServiceProvider();
            await serviceProvider.GetRequiredService<HD2MimntDbContext>().InitializeDatabaseAsync();

            // 预览是长连接会话：由 PreviewWorkerTask 独占读取后续消息，不能再启动取消监听。
            if (command.WorkerType == "Preview")
            {
                await new PreviewWorkerTask(serviceProvider).ExecuteAsync(host, host.CancellationRequested);
                return 0;
            }

            await PrepareStorageStateAsync(command.WorkerType, serviceProvider);

            var watch = host.WatchForCancellationAsync();
            var task = WorkerFactory.Create(command.WorkerType, serviceProvider);
            try
            {
                await task.ExecuteAsync(host, command, host.CancellationRequested);
            }
            catch (OperationCanceledException) when (host.CancellationRequested.IsCancellationRequested)
            {
                await host.SendCancelledAsync();
            }
            await watch;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Worker process failed: {ex}");
            try
            {
                if (host.CancellationRequested.IsCancellationRequested)
                    await host.SendCancelledAsync();
                else
                    await host.SendErrorAsync(ex);
            }
            catch
            {
                // 管道已断开（主程序退出或强杀），无需回执
            }
            return 1;
        }
    }

    /// <summary>
    /// 部署/清理/导入依赖设置缓存、配置初始化和部署状态迁移，启动顺序与 CLI headless 一致；
    /// 纯校验任务不需要这些写入，避免无谓修改数据库。
    /// </summary>
    private static async Task PrepareStorageStateAsync(string workerType, IServiceProvider services)
    {
        if (workerType is not ("Deployment" or "Cleanup" or "Import")) return;

        // 只有部署/清理需要把旧会话记录迁移成当前安装状态；导入不读部署表，跳过整库迁移。
        if (workerType is "Deployment" or "Cleanup")
        {
            var settings = services.GetRequiredService<SettingsService>();
            await services.GetRequiredService<DeploymentStateMigrationService>()
                .EnsureMigratedAsync(settings.GetEffectiveGamePath(), CancellationToken.None);
        }
        await services.GetRequiredService<ProfileService>().InitializeAsync(CancellationToken.None);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 与主程序读同一份定位文件（HD2MIMNT_DATA_PATH 由主进程环境继承），
        // 保证隔离测试与存储搬迁后 Worker 打开的是同一个数据库。
        var storage = new StorageSettingsService(new StorageLocationStore());
        var locations = storage.Current;
        Directory.CreateDirectory(locations.AppDataPath);
        Directory.CreateDirectory(locations.ModsPath);
        Directory.CreateDirectory(locations.CachePath);
        Directory.CreateDirectory(locations.TempPath);
        Directory.CreateDirectory(locations.ImageCachePath);
        Directory.CreateDirectory(locations.DownloadsPath);
        // 与 UI/CLI 一致：用户覆盖的加载器目录也要在子进程生效，否则导入识别和部署排序会用默认目录。
        LoaderRegistry.Initialize(locations.AppDataPath);

        services.AddSingleton(storage);
        services.AddSingleton(new HD2MimntDbContext(locations.DatabasePath));
        services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(locations.DatabasePath));
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();

        services.AddSingleton(sp => new SettingsService(
            sp.GetRequiredService<ISettingsRepository>(), locations.AppDataPath, sp.GetService<ILogger<SettingsService>>()));
        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>()));
        services.AddSingleton(sp => new DeploymentStateMigrationService(
            sp.GetRequiredService<HD2MimntDbContext>(), sp.GetService<ILogger<DeploymentStateMigrationService>>()));

        services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
        services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();
        services.AddSingleton(sp => new Mdl3CompanionService(
            sp.GetService<IAuthenticodeVerifier>() ?? new AuthenticodeVerifier(),
            sp.GetService<ILogger<Mdl3CompanionService>>()));

        services.AddSingleton<IIasSystemService>(sp => new IasSystemService(
            sp.GetRequiredService<ModService>(), sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IIasCompanionService>(sp => new IasCompanionService(
            sp.GetRequiredService<IIasSystemService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<IDeploymentRepository>(),
            sp.GetRequiredService<SettingsService>(),
            Path.Combine(locations.AppDataPath, "IAS-Transactions"),
            Path.Combine(locations.AppDataPath, "Cache", "IAS-Generator"),
            Path.Combine(locations.AppDataPath, "IAS-System", "deployment.json")));

        services.AddSingleton(sp => new DeploymentService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IDeploymentRepository>(),
            sp.GetServices<IDeploymentMethod>(),
            sp.GetService<ILogger<DeploymentService>>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<DeploymentStateMigrationService>(),
            // 与 UI 相同：部署成功后才触发 armor 配套生成，延迟解析避免 ModService↔DeploymentService 构造环。
            () => sp.GetService<IIasCompanionService>(),
            sp.GetRequiredService<Mdl3CompanionService>()));

        services.AddSingleton(sp => new ModService(
            sp.GetRequiredService<IModRepository>(),
            locations.ModsPath,
            sp.GetService<ILogger<ModService>>(),
            sp.GetRequiredService<DeploymentService>(),
            locations.TempPath,
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ProfileService>()));

        services.AddSingleton<IWorkflowRegistry>(_ =>
        {
            var registry = new WorkflowRegistry();
            registry.RegisterWorkflow(new V1Workflow());
            registry.RegisterWorkflow(new LegacyWorkflow());
            registry.RegisterWorkflow(new IntegratedArmorWorkflow());
            return registry;
        });

        // 预览会话所需的读取器/播放引擎只在 Worker 进程里实例化；UI 通过 IPC 使用它们，
        // 关闭预览时整个进程被终止，解析缓冲、解码像素和音频缓存随之释放。
        services.AddSingleton<IGameUnitReferenceReader>(_ => new GameUnitReferenceReader(locations.CachePath));
        services.AddSingleton<IGameSoundBankReader>(sp =>
            (GameUnitReferenceReader)sp.GetRequiredService<IGameUnitReferenceReader>());
        services.AddSingleton<IModelPreviewReader, PatchResourceInspectionService>();
        services.AddSingleton<GameAudioBaselineProvider>();
        services.AddSingleton<IAudioPreviewReader, AudioBankInspectionService>();
        services.AddSingleton<IAudioPlaybackEngine, AudioPlaybackEngine>();
    }
}
