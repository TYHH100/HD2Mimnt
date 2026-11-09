using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.AudioPreview;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Worker;

/// <summary>
/// 预览会话：在独立进程里执行模型解析、贴图解码、音频巡检与播放。请求按到达顺序逐个处理，
/// 大块结果写入进程私有临时文件、只通过管道传路径；主程序关闭预览时直接终止进程，
/// 解析缓冲、解码像素与音频缓存随之立即归还系统。
/// </summary>
public sealed class PreviewWorkerTask(IServiceProvider services)
{
    private readonly IModelPreviewReader _modelReader = services.GetRequiredService<IModelPreviewReader>();
    private readonly IAudioPreviewReader _audioReader = services.GetRequiredService<IAudioPreviewReader>();
    private readonly IAudioPlaybackEngine _playback = services.GetRequiredService<IAudioPlaybackEngine>();
    private readonly GameAudioBaselineProvider _baselines = services.GetRequiredService<GameAudioBaselineProvider>();
    private readonly SettingsService _settings = services.GetRequiredService<SettingsService>();

    private readonly object _currentGate = new();
    private CancellationTokenSource? _currentRequest;
    private string _tempRoot = string.Empty;
    private long _pendingReleaseBytes;

    /// <summary>累计传输超过该阈值后压实一次 LOH：解码像素已经写进临时文件，进程里不再需要。</summary>
    private const long ReleaseThresholdBytes = 64L * 1024 * 1024;

    public async Task ExecuteAsync(WorkerHost host, CancellationToken cancellationToken)
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "HD2Mimnt.Preview", host.TaskId.ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        CleanupStaleSessions();

        void OnPlaybackEnded(AudioEntry entry, string? error) =>
            _ = SendEventAsync(host, PreviewEventTypes.PlaybackEnded,
                new PlaybackEndedEvent
                {
                    Error = error,
                    SourceId = entry.SourceId,
                    BackingFilePath = entry.BackingFilePath,
                    DataOffset = entry.DataOffset
                }, CancellationToken.None);
        _playback.PlaybackEnded += OnPlaybackEnded;

        var channel = Channel.CreateUnbounded<WorkerMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        using var stateTimer = new Timer(_ => ReportPlaybackState(host), null, 100, 100);
        var processor = ProcessRequestsAsync(host, channel.Reader, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await host.ReceiveMessageAsync(cancellationToken);
                if (message is null) break;
                switch (message.MessageType)
                {
                    case PreviewMessageTypes.Request:
                        channel.Writer.TryWrite(message);
                        break;
                    case PreviewMessageTypes.Cancel:
                        CancelCurrentRequest();
                        break;
                    // 关闭预览或主程序取消：立即取消当前加载并结束会话；进程随后被主程序终止。
                    case PreviewMessageTypes.Shutdown:
                    case "Cancel":
                        CancelCurrentRequest();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            channel.Writer.TryComplete();
            CancelCurrentRequest();
            try { await processor.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            catch (Exception) { }
            stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _playback.PlaybackEnded -= OnPlaybackEnded;
            try { _playback.Dispose(); } catch { }
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    private async Task ProcessRequestsAsync(WorkerHost host, ChannelReader<WorkerMessage> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            var request = JsonSerializer.Deserialize<PreviewRequestPayload>(message.Payload ?? "{}");
            if (request is null) continue;
            if (string.IsNullOrEmpty(request.RequestId)) request.RequestId = Guid.NewGuid().ToString("N");

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_currentGate) _currentRequest = requestCancellation;
            PreviewResponsePayload response;
            try
            {
                var payload = await HandleAsync(request, requestCancellation.Token);
                response = new PreviewResponsePayload
                {
                    RequestId = request.RequestId,
                    Success = true,
                    Payload = payload
                };
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                response = new PreviewResponsePayload
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = "cancelled"
                };
            }
            catch (Exception ex)
            {
                response = new PreviewResponsePayload
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Error = ex.Message
                };
            }
            finally
            {
                lock (_currentGate) _currentRequest = null;
            }

            try
            {
                await host.SendMessageAsync(new WorkerMessage
                {
                    MessageType = PreviewMessageTypes.Response,
                    TaskId = host.TaskId,
                    Payload = JsonSerializer.Serialize(response)
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }
            // 响应发出后 handler 的局部大数组已不可达；按累计量压实，避免反复切分辨率
            // 时 Worker 进程的高水位一直不回落。
            ReleaseLargePayloadsIfNeeded();
        }
    }

    private Task<string?> HandleAsync(PreviewRequestPayload request, CancellationToken cancellationToken) =>
        request.Method switch
        {
            PreviewMethods.LoadModel => LoadModelAsync(request.Payload, cancellationToken),
            PreviewMethods.PreviewTexture => PreviewTextureAsync(request.Payload, cancellationToken),
            PreviewMethods.InspectAudio => InspectAudioAsync(request.Payload, cancellationToken),
            PreviewMethods.PlaybackPlay => PlayAsync(request.Payload, cancellationToken),
            PreviewMethods.PlaybackPause => SimplePlaybackCommandAsync(() => _playback.Pause()),
            PreviewMethods.PlaybackResume => SimplePlaybackCommandAsync(() => _playback.Resume()),
            PreviewMethods.PlaybackStop => SimplePlaybackCommandAsync(() => _playback.Stop()),
            PreviewMethods.PlaybackSeek => SeekAsync(request.Payload),
            PreviewMethods.PlaybackSetVolume => SetVolumeAsync(request.Payload),
            _ => throw new InvalidOperationException($"Unknown preview method '{request.Method}'.")
        };

    private async Task<string?> LoadModelAsync(string? payload, CancellationToken cancellationToken)
    {
        var request = Deserialize<ModelPreviewLoadRequest>(payload)
            ?? throw new InvalidDataException("Missing model preview request.");
        var patches = request.PatchFiles.Select(path => new FileInfo(path)).ToArray();
        var result = await _modelReader.PreviewModelAsync(new DirectoryInfo(request.ModDirectory), patches,
            request.ForceDecodeOversizedStreams, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var path = CreatePayloadPath("model", ".bin");
        long packBytes;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   64 * 1024, FileOptions.SequentialScan))
        {
            ModelPreviewPack.Write(stream, result);
            packBytes = stream.Length;
        }
        // 动画原始载荷不在传输包里，UI 也不消费；写完包立即释放，避免每次重载累积 64MB 上限。
        result.PatchAnimationResources.Clear();
        TrackPayload(packBytes);
        return JsonSerializer.Serialize(new ModelPreviewLoadResponse { PackPath = path });
    }

    private async Task<string?> PreviewTextureAsync(string? payload, CancellationToken cancellationToken)
    {
        var request = Deserialize<TexturePreviewRequest>(payload)
            ?? throw new InvalidDataException("Missing texture preview request.");
        var texture = request.Texture ?? throw new InvalidDataException("Missing texture metadata.");
        var decoded = await _modelReader.PreviewTextureAsync(new DirectoryInfo(request.ModDirectory), texture,
            request.MaxPreviewPixels, cancellationToken);
        var bytes = decoded?.BgraPixels ?? decoded?.EncodedImageBytes;
        if (decoded is null || bytes is null)
            return JsonSerializer.Serialize(new TexturePreviewResponse { Found = false });

        var path = CreatePayloadPath("texture", ".bin");
        File.WriteAllBytes(path, bytes);
        TrackPayload(bytes.Length);
        return JsonSerializer.Serialize(new TexturePreviewResponse
        {
            Found = true,
            Width = decoded.Width,
            Height = decoded.Height,
            Description = decoded.Description,
            PayloadKind = decoded.BgraPixels is not null ? "bgra" : "encoded",
            PayloadPath = path
        });
    }

    private async Task<string?> InspectAudioAsync(string? payload, CancellationToken cancellationToken)
    {
        var request = Deserialize<AudioInspectRequest>(payload)
            ?? throw new InvalidDataException("Missing audio preview request.");
        var patches = request.PatchFiles.Select(path => new FileInfo(path)).ToArray();
        var resolver = _baselines.CreateResolver(_settings.GetEffectiveGamePath());
        var result = await _audioReader.InspectAsync(new DirectoryInfo(request.ModDirectory), patches, resolver,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var path = CreatePayloadPath("audio", ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(result));
        return JsonSerializer.Serialize(new AudioInspectResponse { InventoryPath = path });
    }

    private async Task<string?> PlayAsync(string? payload, CancellationToken cancellationToken)
    {
        var request = Deserialize<PlaybackPlayRequest>(payload)
            ?? throw new InvalidDataException("Missing playback request.");
        var entry = request.Entry ?? throw new InvalidDataException("Missing audio entry.");
        var (success, error) = await _playback.PlayAsync(entry, cancellationToken);
        if (success) _playback.SetVolume(request.Volume);
        return JsonSerializer.Serialize(new PlaybackResultResponse { Success = success, Error = error });
    }

    private Task<string?> SimplePlaybackCommandAsync(Action command)
    {
        command();
        return Task.FromResult<string?>(JsonSerializer.Serialize(new PlaybackResultResponse { Success = true }));
    }

    private Task<string?> SeekAsync(string? payload)
    {
        var request = Deserialize<PlaybackSeekRequest>(payload)
            ?? throw new InvalidDataException("Missing seek request.");
        _playback.Seek(request.Seconds);
        return Task.FromResult<string?>(JsonSerializer.Serialize(new PlaybackResultResponse { Success = true }));
    }

    private Task<string?> SetVolumeAsync(string? payload)
    {
        var request = Deserialize<PlaybackVolumeRequest>(payload)
            ?? throw new InvalidDataException("Missing volume request.");
        _playback.SetVolume(request.Volume);
        return Task.FromResult<string?>(JsonSerializer.Serialize(new PlaybackResultResponse { Success = true }));
    }

    private void ReportPlaybackState(WorkerHost host)
    {
        var state = _playback.State;
        if (state is not (AudioPlaybackState.Playing or AudioPlaybackState.Paused))
            return;
        var entry = _playback.CurrentEntry;
        _ = SendEventAsync(host, PreviewEventTypes.PlaybackState, new PlaybackStateEvent
        {
            State = state,
            PositionSeconds = _playback.Position.TotalSeconds,
            DurationSeconds = _playback.Duration.TotalSeconds,
            SourceId = entry?.SourceId ?? 0,
            DataOffset = entry?.DataOffset ?? 0
        }, CancellationToken.None);
    }

    private static async Task SendEventAsync(WorkerHost host, string eventType, object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await host.SendMessageAsync(new WorkerMessage
            {
                MessageType = PreviewMessageTypes.Event,
                TaskId = host.TaskId,
                Payload = JsonSerializer.Serialize(new PreviewEventPayload
                {
                    EventType = eventType,
                    Payload = JsonSerializer.Serialize(payload)
                })
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 主程序已关闭会话
        }
    }

    private void TrackPayload(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _pendingReleaseBytes, bytes);
    }

    private void ReleaseLargePayloadsIfNeeded()
    {
        if (Interlocked.Read(ref _pendingReleaseBytes) < ReleaseThresholdBytes) return;
        Interlocked.Exchange(ref _pendingReleaseBytes, 0);
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            // 立即把已释放的页还给系统，任务管理器里的 Worker 占用才会同步回落。
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Preview payload GC failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);

    private void CancelCurrentRequest()
    {
        lock (_currentGate)
        {
            try { _currentRequest?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private string CreatePayloadPath(string prefix, string extension) =>
        Path.Combine(_tempRoot, $"{prefix}-{Guid.NewGuid():N}{extension}");

    private void CleanupStaleSessions()
    {
        try
        {
            var parent = Directory.GetParent(_tempRoot)?.FullName;
            if (parent is null) return;
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(directory, _tempRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响预览
        }
    }

    private static T? Deserialize<T>(string? payload) =>
        payload is null ? default : JsonSerializer.Deserialize<T>(payload);
}
