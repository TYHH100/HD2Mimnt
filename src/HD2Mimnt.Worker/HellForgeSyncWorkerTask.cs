using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker;

/// <summary>在独立进程把配置模组同步进 HellForge mods\（或清理本管理器同步的模组）；结果状态由 HellForgeSyncResult 决定。</summary>
public sealed class HellForgeSyncWorkerTask(HellForgeModSyncService syncService) : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        var mode = args.GetValueOrDefault("Mode", "Sync");
        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var progress = new ProgressAdapter(reporter);

        HellForgeSyncResult result;
        if (string.Equals(mode, "Remove", StringComparison.OrdinalIgnoreCase))
        {
            result = await syncService.RemoveSyncedAsync(progress, cancellationToken);
        }
        else
        {
            var profileId = ParseGuid(args, "ProfileId");
            var order = ParseEnum<DeploymentOrder>(args, "Order");
            result = await syncService.SyncProfileAsync(profileId, order, progress, cancellationToken);
        }
        reporter.Flush();

        var error = result.ErrorMessage ?? result.Message;
        switch (result.Status)
        {
            case DeploymentStatus.Completed:
                await host.SendCompletedAsync(true, result.Message ?? "同步完成", cancellationToken: cancellationToken);
                break;
            case DeploymentStatus.PartiallyFailed:
                await host.SendCompletedAsync(false, result.Message, [error ?? "同步未完全成功"],
                    partiallyFailed: true, cancellationToken: cancellationToken);
                break;
            case DeploymentStatus.Cancelled:
                await host.SendCancelledAsync();
                break;
            default:
                await host.SendCompletedAsync(false, result.Message, [error ?? "同步失败"], cancellationToken: cancellationToken);
                break;
        }
    }

    private static Guid ParseGuid(Dictionary<string, string> args, string key)
        => args.TryGetValue(key, out var text) && Guid.TryParse(text, out var value)
            ? value : throw new ArgumentException($"HellForge 同步任务缺少有效的 {key} 参数。");

    private static T ParseEnum<T>(Dictionary<string, string> args, string key) where T : struct, Enum
        => args.TryGetValue(key, out var text) && Enum.TryParse<T>(text, out var value)
            ? value : throw new ArgumentException($"HellForge 同步任务缺少有效的 {key} 参数。");

    private sealed class ProgressAdapter(WorkerProgressReporter reporter) : IProgress<HellForgeSyncProgress>
    {
        public void Report(HellForgeSyncProgress value)
        {
            var percent = value.TotalMods > 0 ? value.CompletedMods * 100d / value.TotalMods : 0;
            reporter.Report(Math.Clamp(percent, 0, 100), value.Stage, null, value.Message);
        }
    }
}