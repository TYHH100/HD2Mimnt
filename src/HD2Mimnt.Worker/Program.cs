using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Worker process entry point
/// Executes background tasks in isolated process
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        Guid taskId = Guid.Empty;
        string workerType = string.Empty;
        string pipeName = string.Empty;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--task-id" && i + 1 < args.Length)
            {
                Guid.TryParse(args[i + 1], out taskId);
            }
            else if (args[i] == "--worker" && i + 1 < args.Length)
            {
                workerType = args[i + 1];
            }
            else if (args[i] == "--pipe-name" && i + 1 < args.Length)
            {
                pipeName = args[i + 1];
            }
        }

        if (taskId == Guid.Empty || string.IsNullOrEmpty(workerType) || string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine("Usage: HD2Mimnt.Worker.exe --task-id <guid> --worker <type> --pipe-name <name>");
            return 1;
        }

        WorkerHost? host = null;

        try
        {
            // Setup dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services);
            var serviceProvider = services.BuildServiceProvider();

            // Create worker host and connect to main app
            host = new WorkerHost(taskId, pipeName);
            await host.ConnectAsync(timeoutMilliseconds: 5000);

            // Create and execute worker
            var worker = WorkerFactory.Create(workerType, host, serviceProvider);
            await worker.ExecuteAsync();

            return 0; // Success
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Worker process failed: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);

            if (host != null)
            {
                try
                {
                    await host.SendErrorAsync(ex);
                }
                catch
                {
                    // Failed to send error, pipe may be disconnected
                }
            }

            return 1; // Failure
        }
        finally
        {
            host?.Dispose();
        }
    }

    static void ConfigureServices(IServiceCollection services)
    {
        // Database
        services.AddSingleton<HD2MimntDbContext>();

        // Repositories
        services.AddSingleton<IModRepository, ModRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
    }
}
