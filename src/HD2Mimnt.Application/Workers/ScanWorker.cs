using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Workers;

/// <summary>
/// Scan Worker - 扫描 Mod 目录
/// </summary>
public class ScanWorker : IWorker
{
    private readonly IModRepository _modRepository;

    public string Type => "Scan";

    public ScanWorker(IModRepository modRepository)
    {
        _modRepository = modRepository;
    }

    public async Task ExecuteAsync(
        ModTask task,
        IProgress<TaskProgress> progress,
        CancellationToken cancellationToken)
    {
        // Stage 1: 准备扫描
        progress.Report(new TaskProgress
        {
            Progress = 5,
            Stage = "Preparing",
            Message = "Preparing to scan mod directory..."
        });

        await Task.Delay(300, cancellationToken);

        // Stage 2: 扫描目录
        progress.Report(new TaskProgress
        {
            Progress = 20,
            Stage = "Scanning Directory",
            Message = "Scanning for mod folders..."
        });

        var modFolders = new[] { "mod1", "mod2", "mod3", "mod4", "mod5" }; // 模拟扫描结果
        await Task.Delay(1000, cancellationToken);

        // Stage 3: 处理每个 Mod
        int processed = 0;
        foreach (var folder in modFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report(new TaskProgress
            {
                Progress = 20 + (processed * 60 / modFolders.Length),
                Stage = "Processing Mods",
                CurrentItem = folder,
                Message = $"Processing {folder}..."
            });

            await Task.Delay(500, cancellationToken); // 模拟处理
            processed++;
        }

        // Stage 4: 验证结果
        progress.Report(new TaskProgress
        {
            Progress = 90,
            Stage = "Validating",
            Message = "Validating scan results..."
        });

        await Task.Delay(500, cancellationToken);

        // Stage 5: 完成
        progress.Report(new TaskProgress
        {
            Progress = 100,
            Stage = "Completed",
            Message = "Scan completed successfully"
        });

        task.Result = $"Found {modFolders.Length} mods";
    }
}
