using System.Text.Json;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 播放引擎代理：实际 WASAPI/NVorbis 播放发生在预览 Worker 进程内，这里只转发命令并缓存
/// Worker 推送的状态。关闭预览会终止 Worker，解码缓冲与音频设备随进程一起释放。
/// </summary>
public sealed class RemoteAudioPlaybackEngine : IAudioPlaybackEngine
{
    private readonly IPreviewWorkerSession _session;
    private int _generation;
    private int _state = (int)AudioPlaybackState.Idle;
    private double _positionSeconds;
    private double _durationSeconds;
    private AudioEntry? _currentEntry;
    private float _volume = 1f;

    public RemoteAudioPlaybackEngine(IPreviewWorkerSession session)
    {
        _session = session;
        _session.EventReceived += OnSessionEvent;
    }

    public event Action<AudioEntry, string?>? PlaybackEnded;

    public AudioEntry? CurrentEntry => Volatile.Read(ref _currentEntry);

    public AudioPlaybackState State => (AudioPlaybackState)Volatile.Read(ref _state);

    public TimeSpan Position => TimeSpan.FromSeconds(Volatile.Read(ref _positionSeconds));

    public TimeSpan Duration => TimeSpan.FromSeconds(Volatile.Read(ref _durationSeconds));

    public async Task<(bool Success, string? Error)> PlayAsync(AudioEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StopLocalState(clearEntry: true);
        // 先记录当前条目：极短音频可能在响应到达前就推送 PlaybackEnded，
        // 事件处理需要能把它对应回 UI 的 AudioEntry 实例。
        _currentEntry = entry;
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _state, (int)AudioPlaybackState.Loading);
        try
        {
            var response = await _session.RequestAsync<PlaybackResultResponse>(PreviewMethods.PlaybackPlay,
                new PlaybackPlayRequest { Entry = entry, Volume = _volume }, cancellationToken);
            if (generation != Volatile.Read(ref _generation))
                return (false, null);
            if (response is { Success: true })
            {
                // 极短音频可能在响应到达前就结束：不要把已回落到 Idle 的状态改回 Playing。
                if ((AudioPlaybackState)Volatile.Read(ref _state) == AudioPlaybackState.Loading)
                    Volatile.Write(ref _state, (int)AudioPlaybackState.Playing);
                return (true, null);
            }
            _currentEntry = null;
            Volatile.Write(ref _state, (int)AudioPlaybackState.Idle);
            return (false, response?.Error);
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _currentEntry = null;
                Volatile.Write(ref _state, (int)AudioPlaybackState.Idle);
            }
            return (false, null);
        }
        catch
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _currentEntry = null;
                Volatile.Write(ref _state, (int)AudioPlaybackState.Idle);
            }
            throw;
        }
    }

    public void Pause()
    {
        if (State != AudioPlaybackState.Playing) return;
        _session.Post(PreviewMethods.PlaybackPause);
        Volatile.Write(ref _state, (int)AudioPlaybackState.Paused);
    }

    public void Resume()
    {
        if (State != AudioPlaybackState.Paused) return;
        _session.Post(PreviewMethods.PlaybackResume);
        Volatile.Write(ref _state, (int)AudioPlaybackState.Playing);
    }

    public double Seek(double seconds)
    {
        var total = Duration.TotalSeconds;
        var target = Math.Clamp(seconds, 0, Math.Max(0, total));
        _session.Post(PreviewMethods.PlaybackSeek, new PlaybackSeekRequest { Seconds = target });
        Volatile.Write(ref _positionSeconds, target);
        return target;
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        _session.Post(PreviewMethods.PlaybackSetVolume, new PlaybackVolumeRequest { Volume = _volume });
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _session.Post(PreviewMethods.PlaybackStop);
        StopLocalState(clearEntry: true);
    }

    private void StopLocalState(bool clearEntry)
    {
        Volatile.Write(ref _state, (int)AudioPlaybackState.Idle);
        Volatile.Write(ref _positionSeconds, 0);
        Volatile.Write(ref _durationSeconds, 0);
        if (clearEntry) _currentEntry = null;
    }

    private void OnSessionEvent(PreviewEventPayload previewEvent)
    {
        try
        {
            switch (previewEvent.EventType)
            {
                case PreviewEventTypes.PlaybackState:
                    var state = JsonSerializer.Deserialize<PlaybackStateEvent>(previewEvent.Payload ?? "{}");
                    if (state is null) return;
                    if (state.SourceId != 0 && CurrentEntry is { } playing &&
                        (state.SourceId != playing.SourceId || state.DataOffset != playing.DataOffset))
                        return;
                    Volatile.Write(ref _state, (int)state.State);
                    Volatile.Write(ref _positionSeconds, state.PositionSeconds);
                    Volatile.Write(ref _durationSeconds, state.DurationSeconds);
                    break;
                case PreviewEventTypes.PlaybackEnded:
                    var ended = JsonSerializer.Deserialize<PlaybackEndedEvent>(previewEvent.Payload ?? "{}");
                    // 旧播放的结束事件可能晚于新播放开始，按媒体标识过滤掉。
                    if (ended is not null && CurrentEntry is { } current &&
                        (ended.SourceId != current.SourceId || ended.DataOffset != current.DataOffset))
                        return;
                    StopLocalState(clearEntry: false);
                    if (CurrentEntry is { } entry)
                        PlaybackEnded?.Invoke(entry, ended?.Error);
                    break;
                case PreviewEventTypes.SessionEnded:
                    // Worker 崩溃/退出时没有逐条结束事件，必须主动停掉界面上的播放状态。
                    StopLocalState(clearEntry: false);
                    if (CurrentEntry is { } interrupted)
                        PlaybackEnded?.Invoke(interrupted, "预览进程已退出");
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    public void Dispose() => _session.EventReceived -= OnSessionEvent;
}
