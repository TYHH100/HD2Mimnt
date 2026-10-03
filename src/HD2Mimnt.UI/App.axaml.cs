using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Workers;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace HD2Mimnt.UI;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    private RuntimeMetricsWriter? _runtimeMetricsWriter;

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

            // 初始化数据库
            var dbContext = ServiceProvider.GetRequiredService<HD2MimntDbContext>();
            dbContext.InitializeDatabaseAsync().Wait();

            // 初始化 Task Center 并注册 Workers
            var taskService = ServiceProvider.GetRequiredService<TaskService>();
            var workers = ServiceProvider.GetServices<IWorker>();
            foreach (var worker in workers)
            {
                taskService.RegisterWorker(worker);
            }

            // 创建主窗口
            var mainViewModel = ServiceProvider.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
            mainViewModel.AttachOwner(mainWindow);

            desktop.MainWindow = mainWindow;

            // 初始化 ViewModel - 使用异步启动避免死锁
            _ = mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
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
        services.AddSingleton<IPluginHostManager, PluginHostManager>();

        // 图片服务
        services.AddSingleton<IImageCacheService>(sp =>
            new ImageCacheService(imageCachePath, memoryCacheSize: 100));
        services.AddSingleton<IImageLoaderService, ImageLoaderService>();

        // 应用层
        services.AddSingleton(sp => new ModService(
            sp.GetRequiredService<IModRepository>(),
            modsStoragePath,
            sp.GetService<ILogger<ModService>>()));

        services.AddSingleton(sp => new ProfileService(
            sp.GetRequiredService<IProfileRepository>(),
            sp.GetRequiredService<IModRepository>()));

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

        // UI 层 ViewModels
        services.AddTransient(sp => new LibraryViewModel(
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<SearchService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<DeploymentService>(),
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<ConfirmationService>()));

        services.AddTransient(sp => new ProfileViewModel(
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<ConfirmationService>()));

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
            sp.GetRequiredService<GameDetectionService>(),
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<TaskService>(),
            sp.GetRequiredService<ModService>(),
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<TaskCenterViewModel>(),
            sp.GetRequiredService<NotificationPanelViewModel>(),
            sp.GetRequiredService<ExternalToolsViewModel>(),
            sp.GetRequiredService<PluginManagementViewModel>(),
            sp.GetRequiredService<BackupViewModel>(),
            sp.GetRequiredService<LogViewModel>(),
            sp.GetRequiredService<ConfirmationService>()));
    }
}
