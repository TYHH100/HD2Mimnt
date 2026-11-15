using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// MMD 资源包适配：检查与转换在独立 Worker 中完成，主程序只负责成品入库与配置启用。
/// 与角色模型转换一致，成品以文件夹交付并直接导入管理器库。
/// </summary>
public sealed class MmdAdaptationService : IMmdAdaptationService
{
    private readonly ModService _mods;
    private readonly ProfileService _profiles;
    private readonly TaskService _tasks;

    public MmdAdaptationService(ModService mods, ProfileService profiles, TaskService tasks)
    {
        _mods = mods;
        _profiles = profiles;
        _tasks = tasks;
    }

    public async Task<MmdAdaptationInspection> InspectAsync(string sourcePath, string? sourceOption,
        MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var outcome = await _tasks.RunProcessTaskAsync(
            $"检查 MMD 资源包「{DisplayName(sourcePath)}」", "MmdAdaptation",
            Arguments("inspect", sourcePath, sourceOption, sourceInfo, idlePlayback),
            new ProgressBridge(progress), cancellationToken: token);
        var inspection = ReadResult<MmdInspection>(outcome, "MMD 检查");
        return new MmdAdaptationInspection(inspection.Name, inspection.Source,
            inspection.SourceOptions
                .Select(option => new MmdAdaptationOption(option.Key, option.Name, option.Patches)).ToList(),
            inspection.SelectedOption, inspection.IdlePlayback, inspection.Status,
            inspection.Tracks.Select(track => new MmdAdaptationTrack(track.Id, track.Title, track.Fps,
                track.Duration, track.IdlePlayback, track.Frames, track.Music, track.Camera,
                track.OriginalSlots)).ToList(),
            inspection.Warnings, inspection.HasPreview);
    }

    public async Task<MmdAdaptationResult> ConvertAsync(string sourcePath, string? sourceOption,
        MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "HD2Mimnt-MMD", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(work, "package");
        Directory.CreateDirectory(work);
        try
        {
            var arguments = Arguments("convert", sourcePath, sourceOption, sourceInfo, idlePlayback);
            arguments["OutputDirectory"] = output;
            var outcome = await _tasks.RunProcessTaskAsync(
                $"转换「{DisplayName(sourcePath)}」为 HD2CL MMD 资源包", "MmdAdaptation", arguments,
                new ProgressBridge(progress), cancellationToken: token);
            var artifact = ReadResult<MmdConvertResult>(outcome, "MMD 转换");
            if (!Directory.Exists(artifact.OutputDirectory))
                throw new InvalidOperationException("MMD 转换没有生成成品目录。");

            progress?.Report("导入管理器库…");
            var imported = await _mods.ImportModAsync(artifact.OutputDirectory, token);
            await _mods.AddTagAsync(imported, Hd2ClProductTag.Value, token);
            Guid? profileId = null;
            var current = await _profiles.GetActiveProfileAsync(token);
            if (current is not null)
            {
                profileId = current.Id;
                await _profiles.AddModToProfileAsync(current.Id, imported, token);
                await _profiles.SetModEnabledAsync(current.Id, imported, true, token);
            }
            progress?.Report(current is null
                ? "已导入模组库（当前没有配置文件）"
                : "已导入、加入当前配置并启用");
            return new MmdAdaptationResult(imported, artifact.Title, artifact.Id, artifact.Frames,
                artifact.Duration, profileId);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, string> Arguments(string action, string sourcePath, string? sourceOption,
        MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback)
    {
        var arguments = new Dictionary<string, string>
        {
            ["Action"] = action,
            ["Source"] = sourcePath
        };
        if (!string.IsNullOrWhiteSpace(sourceOption)) arguments["SourceOption"] = sourceOption;
        if (!string.IsNullOrWhiteSpace(idlePlayback)) arguments["IdlePlayback"] = idlePlayback;
        if (sourceInfo is not null) arguments["Metadata"] = JsonSerializer.Serialize(sourceInfo);
        return arguments;
    }

    private static T ReadResult<T>(ProcessTaskOutcome outcome, string action)
    {
        if (outcome.Task?.Status is HD2Mimnt.Core.Entities.TaskStatus.Cancelled
            or HD2Mimnt.Core.Entities.TaskStatus.ForceTerminated)
            throw new OperationCanceledException();
        if (outcome.Task?.Status != HD2Mimnt.Core.Entities.TaskStatus.Completed || string.IsNullOrEmpty(outcome.Data))
            throw new InvalidOperationException(outcome.Task?.Errors.FirstOrDefault()
                ?? outcome.Task?.Message ?? action + "失败。");
        return JsonSerializer.Deserialize<T>(outcome.Data)
               ?? throw new InvalidOperationException(action + "结果无效。");
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    /// <summary>把任务中心的 Worker 进度转成 UI 文本；观察者异常不得影响任务终态。</summary>
    private sealed class ProgressBridge(IProgress<string>? target) : IProgress<TaskProgress>
    {
        public void Report(TaskProgress value)
        {
            if (target is null || string.IsNullOrWhiteSpace(value.Stage)) return;
            try { target.Report(value.Stage); } catch { }
        }
    }
}
