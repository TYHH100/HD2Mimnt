using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker;

/// <summary>
/// 批量校验模组库：按工作流检测并校验每个模组，单项失败不中断整体，
/// 结束时汇总为 Completed / PartiallyFailed。
/// </summary>
public sealed class ValidationWorkerTask(IModRepository modRepository, IWorkflowRegistry workflowRegistry) : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken)
    {
        await host.SendProgressAsync(0, "Loading Mods", message: "Loading mod list...", cancellationToken: cancellationToken);
        var mods = (await modRepository.GetAllAsync(cancellationToken)).ToList();

        var errors = new List<string>();
        var warningCount = 0;

        for (var i = 0; i < mods.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = mods[i];
            var percent = mods.Count == 0 ? 100 : (double)i / mods.Count * 100;
            await host.SendProgressAsync(percent, "Validating Mods", mod.Name,
                $"Validating {mod.Name} ({i + 1}/{mods.Count})", cancellationToken);

            try
            {
                if (string.IsNullOrWhiteSpace(mod.Path) || !Directory.Exists(mod.Path))
                {
                    errors.Add($"{mod.Name}: Mod directory does not exist: {mod.Path}");
                    continue;
                }

                var workflow = await workflowRegistry.DetectWorkflowAsync(mod.Path, cancellationToken);
                if (workflow == null)
                {
                    errors.Add($"{mod.Name}: No matching workflow (unrecognized mod structure)");
                    continue;
                }

                var validation = await workflow.ValidateAsync(mod.Path, cancellationToken);
                warningCount += validation.Warnings.Count;
                foreach (var error in validation.Errors)
                {
                    errors.Add($"{mod.Name}: {error}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{mod.Name}: {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = $"Validated {mods.Count} mods, {errors.Count} errors, {warningCount} warnings";
        await host.SendProgressAsync(100, "Completed", message: result, cancellationToken: cancellationToken);
        await host.SendCompletedAsync(errors.Count == 0, result, errors.Count > 0 ? errors : null,
            partiallyFailed: errors.Count > 0, cancellationToken: cancellationToken);
    }
}