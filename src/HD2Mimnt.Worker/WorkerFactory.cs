using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Worker;

/// <summary>Worker 进程内执行的任务；进度与终态经 WorkerHost 回传主程序。</summary>
public interface IWorkerTask
{
    Task ExecuteAsync(WorkerHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken);
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
            _ => throw new ArgumentException($"Unknown worker type: {workerType}", nameof(workerType))
        };
    }
}