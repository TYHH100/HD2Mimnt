using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Worker;

/// <summary>Worker 任务可用的宿主能力；生产环境由 WorkerHost（命名管道）实现，测试可回环注入。</summary>
public interface IWorkerTaskHost
{
    Guid TaskId { get; }
    CancellationToken CancellationRequested { get; }
    Task SendProgressAsync(double progress, string stage, string? currentItem = null,
        string? message = null, CancellationToken cancellationToken = default);
    Task SendCompletedAsync(bool success, string? result = null, List<string>? errors = null,
        bool partiallyFailed = false, CancellationToken cancellationToken = default, string? data = null);
    Task SendCancelledAsync(CancellationToken cancellationToken = default);
    Task SendErrorAsync(Exception exception, CancellationToken cancellationToken = default);
    Task<WorkerPromptResponse> PromptAsync(string kind, string? title = null, string? payload = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Worker 进程内执行的任务；进度与终态经宿主回传主程序。</summary>
public interface IWorkerTask
{
    Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken);
}

public static class WorkerFactory
{
    public static IWorkerTask Create(string workerType, IServiceProvider services)
    {
        return workerType switch
        {
            "Validation" => new ValidationWorkerTask(
                services.GetRequiredService<IModRepository>(),
                services.GetRequiredService<IWorkflowRegistry>()),
            "Deployment" => new DeploymentWorkerTask(services.GetRequiredService<DeploymentService>()),
            "Cleanup" => new CleanupWorkerTask(services.GetRequiredService<DeploymentService>()),
            "Import" => new ImportWorkerTask(services.GetRequiredService<ModService>()),
            _ => throw new ArgumentException($"Unknown worker type: {workerType}", nameof(workerType))
        };
    }
}