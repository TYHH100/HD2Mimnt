using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Core.IPC;

/// <summary>
/// 预览 Worker 的会话消息类型。与一次性任务不同，预览会话在预览打开期间保持进程存活，
/// 主程序按请求/响应交互，关闭预览时直接终止进程回收全部内存。
/// </summary>
public static class PreviewMessageTypes
{
    public const string Request = "PreviewRequest";
    public const string Response = "PreviewResponse";
    public const string Event = "PreviewEvent";
    public const string Cancel = "PreviewCancel";
    public const string Shutdown = "PreviewShutdown";
}

/// <summary>预览会话支持的方法名。</summary>
public static class PreviewMethods
{
    public const string LoadModel = "LoadModel";
    public const string PreviewTexture = "PreviewTexture";
    public const string InspectAudio = "InspectAudio";
    public const string PlaybackPlay = "PlaybackPlay";
    public const string PlaybackPause = "PlaybackPause";
    public const string PlaybackResume = "PlaybackResume";
    public const string PlaybackStop = "PlaybackStop";
    public const string PlaybackSeek = "PlaybackSeek";
    public const string PlaybackSetVolume = "PlaybackSetVolume";
}

/// <summary>主程序 → Worker 的请求信封；Payload 是方法私有 JSON。</summary>
public sealed class PreviewRequestPayload
{
    public string RequestId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? Payload { get; set; }
}

/// <summary>Worker → 主程序的响应信封；Payload 是方法私有 JSON。</summary>
public sealed class PreviewResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Payload { get; set; }
}

/// <summary>Worker 主动推送的事件（播放结束、播放进度等）。</summary>
public sealed class PreviewEventPayload
{
    public string EventType { get; set; } = string.Empty;
    public string? Payload { get; set; }
}

public static class PreviewEventTypes
{
    /// <summary>播放自然结束或失败；Payload 为 <see cref="PlaybackEndedEvent"/>。</summary>
    public const string PlaybackEnded = "PlaybackEnded";

    /// <summary>播放状态/进度心跳；Payload 为 <see cref="PlaybackStateEvent"/>。</summary>
    public const string PlaybackState = "PlaybackState";

    /// <summary>会话进程退出（崩溃或被关闭）；Payload 为空。</summary>
    public const string SessionEnded = "SessionEnded";
}

public sealed class ModelPreviewLoadRequest
{
    public string ModDirectory { get; set; } = string.Empty;
    public List<string> PatchFiles { get; set; } = [];
    public bool ForceDecodeOversizedStreams { get; set; }
}

/// <summary>模型场景二进制包路径；主程序读取后负责删除。</summary>
public sealed class ModelPreviewLoadResponse
{
    public string PackPath { get; set; } = string.Empty;
}

public sealed class TexturePreviewRequest
{
    public string ModDirectory { get; set; } = string.Empty;
    public TextureInspectionItem? Texture { get; set; }
    public int MaxPreviewPixels { get; set; }
}

/// <summary>单张贴图的原始字节路径；主程序读取后负责删除。</summary>
public sealed class TexturePreviewResponse
{
    public bool Found { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>"bgra" 表示 BGRA 像素缓冲，"encoded" 表示 PNG 等编码图像。</summary>
    public string PayloadKind { get; set; } = string.Empty;
    public string PayloadPath { get; set; } = string.Empty;
}

public sealed class AudioInspectRequest
{
    public string ModDirectory { get; set; } = string.Empty;
    public List<string> PatchFiles { get; set; } = [];
}

/// <summary>音频清单 JSON 文件路径；主程序读取后负责删除。</summary>
public sealed class AudioInspectResponse
{
    public string InventoryPath { get; set; } = string.Empty;
}

public sealed class PlaybackPlayRequest
{
    public AudioEntry? Entry { get; set; }

    /// <summary>开始播放后立即应用的音量（0..1）；Worker 每次新建输出设备都要重设。</summary>
    public float Volume { get; set; } = 1f;
}

public sealed class PlaybackResultResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public sealed class PlaybackSeekRequest
{
    public double Seconds { get; set; }
}

public sealed class PlaybackVolumeRequest
{
    public float Volume { get; set; }
}

public sealed class PlaybackStateEvent
{
    public AudioPlaybackState State { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>当前媒体标识；主程序用它忽略旧播放迟到的进度心跳。</summary>
    public ulong SourceId { get; set; }
    public long DataOffset { get; set; }
}

public sealed class PlaybackEndedEvent
{
    public string? Error { get; set; }

    /// <summary>结束的媒体标识；主程序用它过滤旧播放迟到的结束事件。</summary>
    public ulong SourceId { get; set; }
    public string? BackingFilePath { get; set; }
    public long DataOffset { get; set; }
}
