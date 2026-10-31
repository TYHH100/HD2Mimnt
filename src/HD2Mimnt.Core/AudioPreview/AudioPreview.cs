using System.IO;

namespace HD2Mimnt.Core.AudioPreview;

public enum AudioEntryOrigin
{
    /// <summary>Wwise bank 的 DATA chunk 内嵌媒体。</summary>
    BankMedia,

    /// <summary>以独立 TOC 资源存放在 .stream 伴生文件中的媒体。</summary>
    StreamMedia,
}

public enum AudioEntryIssue
{
    None,
    /// <summary>媒体不以 RIFF/WAVE 头开始。</summary>
    NotRiff,
    /// <summary>RIFF WEM 存在但 fmt chunk 不是 Wwise/Ogg Vorbis 编码。</summary>
    NotVorbis,
    /// <summary>WEM 头声明的数据量超过模组实际存储（prefetch 媒体）。</summary>
    Truncated,
    /// <summary>无法在记录的偏移处读取 backing 文件。</summary>
    ReadFailed,
}

public enum AudioBaselineLookup
{
    Found,
    /// <summary>资源存在但缺少该 source id（模组新增媒体）。</summary>
    ResourceMissing,
    /// <summary>基线无法回答（布局/解析失败、资源不存在或比对预算耗尽）。</summary>
    Unavailable,
}

/// <summary>
/// 一个在模组 patch 内发现的（可播放或已识别但不受支持的）WEM 媒体条目。
/// 检查阶段从不加载媒体数据：<see cref="DataOffset"/>/<see cref="SizeBytes"/> 指向
/// <see cref="BackingFilePath"/> 内的切片，由播放引擎按需读取。
/// </summary>
public sealed record AudioEntry(
    ulong SourceId,
    AudioEntryOrigin Origin,
    string PatchRelativePath,
    string? BankName,
    ulong BankFileId,
    string BackingFilePath,
    long DataOffset,
    long SizeBytes,
    int Channels,
    int SampleRate,
    AudioEntryIssue Issue,
    bool? MatchesOriginal = null)
{
    public bool IsPlayable => Issue == AudioEntryIssue.None;
}

/// <summary>展示分组：一个 Wwise bank（合并同补丁的流媒体），或无 bank 补丁的独立流媒体。</summary>
public sealed record AudioBankGroup(
    string PatchRelativePath,
    string? BankName,
    ulong BankFileId,
    IReadOnlyList<AudioEntry> Entries);

public sealed record AudioInventoryResult(
    IReadOnlyList<AudioBankGroup> Groups,
    int PatchCount,
    string? Error,
    int UncomparedEntries = 0)
{
    public static readonly AudioInventoryResult Empty = new([], 0, null);
}

/// <summary>一个游戏原版音频包（如 9ba626afa44a3aa3）的只读比对基线。实现持有打开的资源句柄，用完必须 Dispose。</summary>
public interface IGameAudioBaseline : IDisposable
{
    /// <summary>查询 bank 媒体的原始尺寸；不消耗读取预算。</summary>
    AudioBaselineLookup TryGetBankMediaSize(ulong bankFileId, uint sourceId, out uint size);

    /// <summary>查询流媒体的原始尺寸；不消耗读取预算。</summary>
    AudioBaselineLookup TryGetStreamMediaSize(ulong streamFileId, out uint size);

    /// <summary>计算 bank 媒体的原始 SHA-256（消耗比对预算，结果按 source id 缓存）。</summary>
    byte[]? TryGetBankMediaHash(ulong bankFileId, uint sourceId);

    /// <summary>计算流媒体的原始 SHA-256（消耗比对预算，每次直读）。</summary>
    byte[]? TryGetStreamMediaHash(ulong streamFileId);
}

public interface IAudioPreviewReader
{
    /// <summary>巡检一组模组 patch 文件的音频内容。baselineResolver 按 patch 包基名
    /// （如 9ba626afa44a3aa3）提供原版基线；传 null 时所有条目 MatchesOriginal 均为 null。</summary>
    Task<AudioInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, IGameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken = default);
}

public enum AudioPlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
}

public interface IAudioPlaybackEngine : IDisposable
{
    /// <summary>在音频线程触发：当前播放自然结束或失败。error 为 null 表示自然播完。</summary>
    event Action<AudioEntry, string?>? PlaybackEnded;

    AudioEntry? CurrentEntry { get; }
    AudioPlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    void SetVolume(float volume);

    /// <summary>开始（或替换）条目播放。媒体无法读取/转换/解码时返回 false 与错误消息。</summary>
    Task<(bool Success, string? Error)> PlayAsync(AudioEntry entry, CancellationToken cancellationToken);

    void Pause();
    void Resume();

    /// <summary>在当前流内跳转，返回钳制后的秒数。</summary>
    double Seek(double seconds);

    void Stop();
}
