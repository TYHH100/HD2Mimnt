using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Workers;

/// <summary>
/// Import Worker - 处理 Mod 导入任务
/// </summary>
public class ImportWorker : IWorker
{
    private readonly IModRepository _modRepository;

    public string Type => "Import";

    public ImportWorker(IModRepository modRepository)
    {
        _modRepository = modRepository;
    }

    public async Task ExecuteAsync(
        ModTask task,
        IProgress<TaskProgress> progress,
        CancellationToken cancellationToken)
    {
        // Stage 1: 验证路径
        progress.Report(new TaskProgress
        {
            Progress = 10,
            Stage = "Validating",
            Message = "Validating mod path..."
        });

        await Task.Delay(500, cancellationToken); // 模拟验证

        // Stage 2: 扫描文件
        progress.Report(new TaskProgress
        {
            Progress = 30,
            Stage = "Scanning",
            Message = "Scanning mod files..."
        });

        await Task.Delay(1000, cancellationToken); // 模拟扫描

        // Stage 3: 读取 Metadata
        progress.Report(new TaskProgress
        {
            Progress = 50,
            Stage = "Reading Metadata",
            Message = "Reading mod metadata..."
        });

        await Task.Delay(800, cancellationToken);

        // Stage 4: 创建 Mod 实体
        progress.Report(new TaskProgress
        {
            Progress = 70,
            Stage = "Creating Mod",
            Message = "Creating mod entry..."
        });

        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Imported Mod",
            Author = "Unknown",
            Version = "1.0.0",
            Description = "Imported via task system",
            Path = "/path/to/mod",
            Category = ModCategory.Gameplay,
            Tags = new List<string> { "imported" },
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };

        await _modRepository.AddAsync(mod, cancellationToken);

        // Stage 5: 完成
        progress.Report(new TaskProgress
        {
            Progress = 100,
            Stage = "Completed",
            Message = "Import completed successfully"
        });

        task.Result = $"Successfully imported mod: {mod.Name}";
    }
}
