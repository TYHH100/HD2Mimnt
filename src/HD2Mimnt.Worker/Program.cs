using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Storage;
using HD2Mimnt.Infrastructure.Workflows;
using HD2Mimnt.Worker;
using Microsoft.Extensions.DependencyInjection;

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

    private static void ConfigureServices(IServiceCollection services)
    {
        // 与主程序读同一份定位文件（HD2MIMNT_DATA_PATH 由主进程环境继承），
        // 保证隔离测试与存储搬迁后 Worker 打开的是同一个数据库。
        var storage = new StorageSettingsService(new StorageLocationStore());

        services.AddSingleton(new HD2MimntDbContext(storage.Current.DatabasePath));
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IWorkflowRegistry>(_ =>
        {
            var registry = new WorkflowRegistry();
            registry.RegisterWorkflow(new V1Workflow());
            registry.RegisterWorkflow(new LegacyWorkflow());
            registry.RegisterWorkflow(new IntegratedArmorWorkflow());
            return registry;
        });
    }
}