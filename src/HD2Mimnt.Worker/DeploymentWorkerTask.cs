using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker;

/// <summary>按配置部署模组；部署结果的状态由 DeploymentResult 决定，不依赖异常。</summary>
public sealed class DeploymentWorkerTask(DeploymentService deploymentService) : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        var profileId = ParseGuid(args, "ProfileId");
        var method = ParseEnum<DeploymentMethod>(args, "Method");
        var order = ParseEnum<DeploymentOrder>(args, "Order");
        if (!args.TryGetValue("GamePath", out var gamePath) || string.IsNullOrWhiteSpace(gamePath))
            throw new ArgumentException("部署任务缺少 GamePath 参数。");

        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var result = await deploymentService.DeployProfileAsync(profileId, method, order, gamePath,
            new ProgressAdapter(reporter), cancellationToken);
        reporter.Flush();

        var error = result.ErrorMessage ?? result.Message;
        switch (result.Status)
        {
            case DeploymentStatus.Completed:
                await host.SendCompletedAsync(true,
                    result.Message ?? $"已部署 {result.CompletedMods} 个模组", cancellationToken: cancellationToken);
                break;
            case DeploymentStatus.PartiallyFailed:
                await host.SendCompletedAsync(false, result.Message,
                    [error ?? "部署未完全成功"], partiallyFailed: true, cancellationToken);
                break;
            case DeploymentStatus.Cancelled:
                await host.SendCancelledAsync();
                break;
            default:
                await host.SendCompletedAsync(false, result.Message,
                    [error ?? "部署失败"], cancellationToken: cancellationToken);
                break;
        }
    }

    private static Guid ParseGuid(Dictionary<string, string> args, string key)
        => args.TryGetValue(key, out var text) && Guid.TryParse(text, out var value)
            ? value : throw new ArgumentException($"部署任务缺少有效的 {key} 参数。");

    private static T ParseEnum<T>(Dictionary<string, string> args, string key) where T : struct, Enum
        => args.TryGetValue(key, out var text) && Enum.TryParse<T>(text, out var value)
            ? value : throw new ArgumentException($"部署任务缺少有效的 {key} 参数。");

    private sealed class ProgressAdapter(WorkerProgressReporter reporter) : IProgress<DeploymentProgress>
    {
        public void Report(DeploymentProgress value)
        {
            var itemPercent = value.CurrentModTotalFiles > 0
                ? value.CurrentModFiles * 100d / value.CurrentModTotalFiles
                : 0;
            var percent = value.TotalMods > 0
                ? (value.CompletedMods * 100d + itemPercent) / value.TotalMods
                : 0;
            reporter.Report(Math.Clamp(percent, 0, 100), value.Stage, value.CurrentMod, value.Message);
        }
    }
}
