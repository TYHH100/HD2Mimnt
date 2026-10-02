using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Workers;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace HD2Mimnt.UI;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }

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
            var mainWindow = new MainWindow
            {
                DataContext = ServiceProvider.GetRequiredService<MainWindowViewModel>()
            };

            desktop.MainWindow = mainWindow;

            // 初始化 ViewModel
            var mainViewModel = (MainWindowViewModel)mainWindow.DataContext!;
            mainViewModel.InitializeAsync().Wait();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 获取应用程序数据目录
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HD2Mimnt");
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
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // 基础设施层
        services.AddSingleton(sp => new HD2MimntDbContext(dbPath));
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<ITaskRepository, TaskRepository>();

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

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IModRepository>(),
            sp.GetService<ILogger<SearchService>>()));

        // Task Center
        services.AddSingleton<TaskService>();

        // Workers
        services.AddSingleton<IWorker, ImportWorker>();
        services.AddSingleton<IWorker, ScanWorker>();
        services.AddSingleton<IWorker, ValidationWorker>();

        // UI 层 ViewModels
        services.AddTransient(sp => new LibraryViewModel(
            sp.GetRequiredService<ModService>()));

        services.AddTransient(sp => new ProfileViewModel(
            sp.GetRequiredService<ProfileService>(),
            sp.GetRequiredService<ModService>()));

        services.AddTransient<MainWindowViewModel>();
    }
}
