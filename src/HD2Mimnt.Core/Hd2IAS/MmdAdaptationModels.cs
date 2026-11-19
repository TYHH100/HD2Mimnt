namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>MMD 原始资源包在清单里可选的一组动作补丁（多加载选项时由用户选择）。</summary>
public sealed record MmdAdaptationOption(string Key, string Name, IReadOnlyList<string> Patches);

/// <summary>检查出的曲目信息：时长、帧率、是否含音乐/镜头，以及原包动作槽位。</summary>
public sealed record MmdAdaptationTrack(
    string Id, string Title, int Fps, double Duration, string IdlePlayback, int Frames,
    bool Music, bool Camera, IReadOnlyList<string> OriginalSlots);

public static class MmdAdaptationStatus
{
    public const string Ready = "ready";
    public const string NeedsSelection = "needs_selection";
}

/// <summary>MMD 检查结果；Status 为 ready 时才可转换。</summary>
public sealed record MmdAdaptationInspection(
    string Name, string Source, IReadOnlyList<MmdAdaptationOption> SourceOptions, string? SelectedOption,
    string IdlePlayback, string Status, IReadOnlyList<MmdAdaptationTrack> Tracks,
    IReadOnlyList<string> Warnings, bool HasPreview);

/// <summary>原包缺少 manifest 时补充的来源信息；有清单时只使用图标覆盖。</summary>
public sealed record MmdAdaptationSourceInfo(string Name, string? Key, string? Author, string? Icon, string? Description);

/// <summary>MMD 适配包转换结果：成品已导入模组库并（有配置时）加入当前配置。</summary>
public sealed record MmdAdaptationResult(
    Guid ModId, string Name, string Identity, int Frames, double Duration, Guid? ProfileId);

/// <summary>MMD 导出结果：成品名与写出的 7z 路径（未导入模组库）。</summary>
public sealed record MmdAdaptationExportResult(string Name, string ArchivePath);

public interface IMmdAdaptationService
{
    /// <summary>在独立 Worker 中检查 MMD 原始资源包，返回曲目、媒体与来源选项。</summary>
    Task<MmdAdaptationInspection> InspectAsync(string sourcePath, string? sourceOption,
        MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback, IProgress<string>? progress = null,
        CancellationToken token = default);

    /// <summary>转换并在完成后导入模组库；存在当前配置时把成品加入并启用。</summary>
    Task<MmdAdaptationResult> ConvertAsync(string sourcePath, string? sourceOption,
        MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback, IProgress<string>? progress = null,
        CancellationToken token = default);

    /// <summary>转换并把成品打成 7z 写到 outputDirectory：不导入模组库、不修改当前配置。</summary>
    Task<MmdAdaptationExportResult> ExportAsync(string sourcePath, string outputDirectory,
        string? sourceOption, MmdAdaptationSourceInfo? sourceInfo, string? idlePlayback,
        IProgress<string>? progress = null, CancellationToken token = default);
}
