using HD2Mimnt.Application.Cli;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.NativeCompanion;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Cli;

/// <summary>
/// 管理器未运行时的本地执行宿主：按与 UI 启动相同的存储路径和服务图构建最小容器，
/// 在 CLI 进程内直接执行命令。UI 在运行时仍优先走管道，避免两个进程各自持有内存状态。
/// </summary>
internal static class HeadlessHost
{
    public static async Task<CliCommandResponse> ExecuteAsync(
        IReadOnlyList<string> tokens, bool json, bool yes, CancellationToken cancellationToken)
    {
        var storageSettings = new StorageSettingsService(new StorageLocationStore());
        // 与 UI 启动顺序一致：先完成上次提交的存储迁移，再按最终目录创建服务。
        await storageSettings.InitializeAsync(cancellationToken);

        var services = new ServiceCollection();
        ConfigureServices(services, storageSettings);
        await using var provider = services.BuildServiceProvider();

        var dbContext = provider.GetRequiredService<HD2MimntDbContext>();
        await dbContext.InitializeDatabaseAsync(cancellationToken);

        var settings = provider.GetRequiredService<SettingsService>();
        await provider.GetRequiredService<DeploymentStateMigrationService>()
            .EnsureMigratedAsync(settings.GetEffectiveGamePath(), cancellationToken);
        await provider.GetRequiredService<ProfileService>().InitializeAsync(cancellationToken);

        return await CliCommandCatalog.Build().DispatchAsync(tokens, provider, json, yes, cancellationToken);
    }

    private static void ConfigureServices(IServiceCollection services, StorageSettingsService storageSettings)
    {
        var locations = storageSettings.Current;
        Directory.CreateDirectory(locations.AppDataPath);
        Directory.CreateDirectory(locations.ModsPath);
        Directory.CreateDirectory(locations.ImageCachePath);
        Directory.CreateDirectory(locations.DownloadsPath);
        Directory.CreateDirectory(locations.TempPath);
        LoaderRegistry.Initialize(locations.AppDataPath);

        services.AddSingleton(storageSettings);
        // 无控制台日志提供程序：命令结果统一走 stdout/JSON，避免日志污染输出。
        services.AddLogging();

        services.AddSingleton(sp => new HD2MimntDbContext(locations.DatabasePath));
        services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(locations.DatabasePath));
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<ICompatibilityResultStore, CompatibilityResultRepository>();

        services.AddSingleton(sp => new SettingsService(
            sp.GetRequiredService<ISettingsRepository>(), locations.AppDataPath, sp.GetService<ILogger<SettingsService>>()));
        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<DeploymentStateMigrationService>();

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

        services.AddSingleton<IGameUnitReferenceReader>(_ => new GameUnitReferenceReader(locations.CachePath));
        services.AddSingleton<IModCompatibilityAnalyzer, ModCompatibilityAnalyzer>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<ICompatibilityResourceNames>(sp => new CompatibilityResourceNameProvider(
            sp.GetRequiredService<HttpClient>(), locations.TempPath));
        services.AddSingleton<ModCompatibilityService>();
    }
}
