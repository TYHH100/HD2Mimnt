using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Worker;

/// <summary>
/// 批量导入模组。解压、清单识别、落库都在子进程完成；压缩包密码和 HD2IR 组件确认
/// 需要用户决定，经 IPC 回到主程序弹窗，Worker 只等待结果。
/// </summary>
public sealed class ImportWorkerTask(ModService modService) : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command, CancellationToken cancellationToken)
    {
        var paths = ParsePaths(command.Arguments);
        if (paths.Count == 0) throw new ArgumentException("导入任务缺少 Paths 参数。");
        var sourceLink = ParseSourceLink(command.Arguments);

        using var passwordScope = PasswordAwareArchiveExtractor.BeginRequest(
            (name, token) => RequestPasswordAsync(host, name, token));

        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var importedIds = new List<Guid>();
        var errors = new List<string>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var offset = index * 100d / paths.Count;
            var span = 100d / paths.Count;
            reporter.Report(offset, "准备添加", fileName, path);
            var progress = new ProgressAdapter(reporter, offset, span, fileName);

            try
            {
                var modId = await modService.ImportModAsync(path, cancellationToken, progress, sourceLink,
                    (preview, token) => ConfirmHd2IrAsync(host, preview, token));
                importedIds.Add(modId);
                reporter.Report(offset + span, "导入完成", fileName, "已导入");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}：{ex.Message}");
            }
        }

        reporter.Flush();
        await host.SendCompletedAsync(errors.Count == 0,
            $"已导入 {importedIds.Count} 个模组，失败 {errors.Count} 个",
            errors.Count > 0 ? errors : null,
            partiallyFailed: errors.Count > 0,
            cancellationToken: cancellationToken,
            data: JsonSerializer.Serialize(importedIds));
    }

    private static List<string> ParsePaths(Dictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("Paths", out var json) || string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private static ModPlatformLink? ParseSourceLink(Dictionary<string, string> arguments)
        => arguments.TryGetValue("SourceLink", out var json) && !string.IsNullOrWhiteSpace(json)
            ? JsonSerializer.Deserialize<ModPlatformLink>(json)
            : null;

    private static async Task<string> RequestPasswordAsync(IWorkerTaskHost host, string name, CancellationToken cancellationToken)
    {
        var response = await host.PromptAsync("ArchivePassword", name, cancellationToken: cancellationToken);
        if (!response.Accepted || string.IsNullOrWhiteSpace(response.Value))
            throw new OperationCanceledException(cancellationToken);
        return response.Value;
    }

    private static async Task ConfirmHd2IrAsync(IWorkerTaskHost host, Hd2IrImportPreview preview, CancellationToken cancellationToken)
    {
        var response = await host.PromptAsync("Hd2IrConfirm", preview.Name,
            JsonSerializer.Serialize(preview), cancellationToken);
        if (!response.Accepted) throw new OperationCanceledException(cancellationToken);
    }

    private sealed class ProgressAdapter(WorkerProgressReporter reporter, double offset, double span, string fileName)
        : IProgress<ModImportProgress>
    {
        public void Report(ModImportProgress value)
            => reporter.Report(offset + span * Math.Clamp(value.Percentage, 0, 100) / 100,
                value.Step, fileName, value.Detail);
    }
}
