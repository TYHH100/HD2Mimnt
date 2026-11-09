using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker;

/// <summary>清理最近一次部署产生的目标文件与部署记录（undeploy）。</summary>
public sealed class CleanupWorkerTask(DeploymentService deploymentService) : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken)
    {
        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var progress = new ProgressAdapter(reporter);
        var (filesDeleted, recordsDeleted) = await deploymentService.PreDeploymentCleanupAsync(
            progress, cancellationToken, progress);
        reporter.Flush();
        await host.SendCompletedAsync(true, $"已清理 {filesDeleted} 个文件，{recordsDeleted} 条部署记录",
            cancellationToken: cancellationToken);
    }

    private sealed class ProgressAdapter(WorkerProgressReporter reporter)
        : IProgress<string>, IProgress<DeploymentCleanupProgress>
    {
        public void Report(string value) => reporter.Report(0, "Cleaning", message: value);

        public void Report(DeploymentCleanupProgress value)
        {
            if (value.TotalFiles == 0)
            {
                reporter.Report(0, "Cleaning");
                return;
            }
            var percent = value.ProcessedFiles * 100d / value.TotalFiles;
            reporter.Report(percent, "Cleaning", $"{value.ProcessedFiles:N0} / {value.TotalFiles:N0}");
        }
    }
}
