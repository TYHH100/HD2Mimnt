using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Workers;

/// <summary>
/// Validation Worker - 验证 Mod 完整性
/// </summary>
public class ValidationWorker : IWorker
{
    private readonly IModRepository _modRepository;

    public string Type => "Validation";

    public ValidationWorker(IModRepository modRepository)
    {
        _modRepository = modRepository;
    }

    public async Task ExecuteAsync(
        ModTask task,
        IProgress<TaskProgress> progress,
        CancellationToken cancellationToken)
    {
        // Stage 1: 加载 Mod 列表
        progress.Report(new TaskProgress
        {
            Progress = 10,
            Stage = "Loading Mods",
            Message = "Loading mod list..."
        });

        var mods = await _modRepository.GetAllAsync(cancellationToken);
        var modList = mods.ToList();

        // Stage 2: 验证每个 Mod
        int validated = 0;
        int errors = 0;

        foreach (var mod in modList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report(new TaskProgress
            {
                Progress = 10 + (validated * 80 / modList.Count),
                Stage = "Validating Mods",
                CurrentItem = mod.Name,
                Message = $"Validating {mod.Name}..."
            });

            await Task.Delay(300, cancellationToken); // 模拟验证

            // 模拟随机错误
            if (validated % 7 == 0)
            {
                task.Errors.Add($"Validation error in {mod.Name}: Missing required file");
                errors++;
            }

            validated++;
        }

        // Stage 3: 生成报告
        progress.Report(new TaskProgress
        {
            Progress = 95,
            Stage = "Generating Report",
            Message = "Generating validation report..."
        });

        await Task.Delay(500, cancellationToken);

        // Stage 4: 完成
        progress.Report(new TaskProgress
        {
            Progress = 100,
            Stage = "Completed",
            Message = "Validation completed"
        });

        task.Result = $"Validated {validated} mods, {errors} errors found";

        if (errors > 0)
        {
            task.Status = Core.Entities.TaskStatus.PartiallyFailed;
        }
    }
}
