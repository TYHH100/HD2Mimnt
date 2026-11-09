using System.IO;
using HD2Mimnt.Core.AudioPreview;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NVorbis;
using Ww2Ogg.Core;

namespace HD2Mimnt.Infrastructure.AudioPreview;

/// <summary>
/// 播放单个 <see cref="AudioEntry"/>：从模组的 patch 或 .stream 文件读取 WEM 切片，经
/// ww2ogg 的 .NET 移植转成 Ogg Vorbis（HD2 音频用 aoTuV codebook 编码，先试 AoTuV 再试
/// 普通 packed codebook —— VorbisReader 构造函数会完整解析 setup 头，因此可用来验证选择），
/// 再经 NVorbis 解码、WASAPI 渲染。同一时间只播放一个条目；开始新的播放会停掉上一个。
/// </summary>
public sealed class AudioPlaybackEngine(ILogger<AudioPlaybackEngine>? logger = null) : IAudioPlaybackEngine
{
    private readonly object _gate = new();
    private int _generation;
    private IWavePlayer? _output;
    private VorbisReader? _reader;
    private Stream? _oggStream;
    private Action<float>? _volumeSetter;
    private AudioEntry? _currentEntry;
    private int _state = (int)AudioPlaybackState.Idle;

    /// <summary>在音频线程触发：当前播放自然结束或失败。entry 是正在播放的条目；error 为 null 表示自然播完。</summary>
    public event Action<AudioEntry, string?>? PlaybackEnded;

    public AudioEntry? CurrentEntry => Volatile.Read(ref _currentEntry);

    public AudioPlaybackState State => (AudioPlaybackState)Volatile.Read(ref _state);

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _reader?.SamplePosition >= 0 ? TimeSpan.FromSeconds(_reader.SamplePosition / (double)_reader.SampleRate) : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return _reader?.TotalTime ?? TimeSpan.Zero;
            }
        }
    }

    public void SetVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        lock (_gate)
        {
            _volumeSetter?.Invoke(volume);
        }
    }

    /// <summary>开始（或替换）条目播放。媒体无法读取/转换/解码时返回 false 与错误消息。</summary>
    public async Task<(bool Success, string? Error)> PlayAsync(AudioEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Stop(replaceEntry: true);
        Volatile.Write(ref _state, (int)AudioPlaybackState.Loading);
        var generation = Interlocked.Increment(ref _generation);

        try
        {
            return await Task.Run(() => StartCore(entry, generation, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (false, null);
        }
    }

    private (bool Success, string? Error) StartCore(AudioEntry entry, int generation, CancellationToken cancellationToken)
    {
        byte[] wemData;
        try
        {
            wemData = ReadWemSlice(entry);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to read WEM media at {Offset} from {File}", entry.DataOffset, entry.BackingFilePath);
            FinishGeneration(generation, AudioPlaybackState.Idle);
            return (false, ex.Message);
        }

        string? lastError = null;
        foreach (var codebook in new[] { CodebookLibrary.AoTuV, CodebookLibrary.Default })
        {
            cancellationToken.ThrowIfCancellationRequested();
            VorbisReader? reader = null;
            Stream? oggStream = null;
            try
            {
                oggStream = new MemoryStream();
                using var input = new MemoryStream(wemData, writable: false);
                new WwiseRiffVorbis(input, codebook).GenerateOgg(oggStream);
                oggStream.Position = 0;
                reader = new VorbisReader(oggStream);
                if (reader.Channels <= 0 || reader.SampleRate <= 0)
                    throw new InvalidDataException("Invalid Vorbis stream metadata.");

                if (generation != Volatile.Read(ref _generation))
                {
                    reader.Dispose();
                    oggStream.Dispose();
                    return (false, null);
                }

                lock (_gate)
                {
                    _reader = reader;
                    _oggStream = oggStream;
                    _currentEntry = entry;
                    _volumeSetter = null;
                    _output = CreateOutput(reader, out var volumeSetter);
                    _volumeSetter = volumeSetter;
                }

                Volatile.Write(ref _state, (int)AudioPlaybackState.Playing);
                _output.Play();
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                reader?.Dispose();
                oggStream?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                oggStream?.Dispose();
                lastError = ex.Message;
                logger?.LogDebug(ex, "WEM conversion/decode with {Codebook} failed", codebook == CodebookLibrary.AoTuV ? "AoTuV" : "Default");
            }
        }

        FinishGeneration(generation, AudioPlaybackState.Idle);
        return (false, lastError);
    }

    private IWavePlayer CreateOutput(VorbisReader reader, out Action<float> volumeSetter)
    {
        var sampleProvider = new NvorbisFloatSampleProvider(reader);
        WasapiOut? wasapi = null;
        try
        {
            wasapi = new WasapiOut(AudioClientShareMode.Shared, 200);
            wasapi.Init(sampleProvider);
            volumeSetter = value => wasapi.Volume = value;
            wasapi.PlaybackStopped += OnOutputStopped;
            return wasapi;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "WASAPI output unavailable, falling back to WaveOutEvent");
            wasapi?.Dispose();

            var waveOut = new WaveOutEvent();
            waveOut.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(sampleProvider));
            volumeSetter = value => waveOut.Volume = value;
            waveOut.PlaybackStopped += OnOutputStopped;
            return waveOut;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if ((AudioPlaybackState)Volatile.Read(ref _state) == AudioPlaybackState.Playing)
            {
                _output?.Pause();
                Volatile.Write(ref _state, (int)AudioPlaybackState.Paused);
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if ((AudioPlaybackState)Volatile.Read(ref _state) == AudioPlaybackState.Paused)
            {
                _output?.Play();
                Volatile.Write(ref _state, (int)AudioPlaybackState.Playing);
            }
        }
    }

    /// <summary>在当前流内跳转。返回钳制后的秒数。</summary>
    public double Seek(double seconds)
    {
        lock (_gate)
        {
            if (_reader is null)
                return 0;
            var total = _reader.TotalTime.TotalSeconds;
            var target = Math.Clamp(seconds, 0, Math.Max(0, total));
            _reader.SamplePosition = (long)(target * _reader.SampleRate);
            return _reader.SamplePosition / (double)_reader.SampleRate;
        }
    }

    public void Stop() => Stop(replaceEntry: true);

    private void Stop(bool replaceEntry)
    {
        Interlocked.Increment(ref _generation);
        IWavePlayer? output;
        VorbisReader? reader;
        Stream? oggStream;
        lock (_gate)
        {
            output = _output;
            reader = _reader;
            oggStream = _oggStream;
            _output = null;
            _reader = null;
            _oggStream = null;
            _volumeSetter = null;
            _state = (int)AudioPlaybackState.Idle;
            if (replaceEntry)
                _currentEntry = null;
        }

        if (output is null)
            return;
        try
        {
            output.PlaybackStopped -= OnOutputStopped;
            output.Stop();
            output.Dispose();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to dispose audio output");
        }
        reader?.Dispose();
        oggStream?.Dispose();
    }

    private void FinishGeneration(int generation, AudioPlaybackState state)
    {
        if (generation == Volatile.Read(ref _generation))
            Volatile.Write(ref _state, (int)state);
    }

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        // 自然播完：拆掉播放器并通知。
        var entry = Volatile.Read(ref _currentEntry);
        Stop(replaceEntry: false);
        if (entry is not null)
            PlaybackEnded?.Invoke(entry, e.Exception?.Message);
    }

    public void Dispose() => Stop();

    private static byte[] ReadWemSlice(AudioEntry entry)
    {
        using var stream = new FileStream(
            entry.BackingFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.SequentialScan);
        if (entry.DataOffset < 0 || entry.SizeBytes <= 0 || entry.DataOffset + entry.SizeBytes > stream.Length)
            throw new IOException("Audio media lies outside the backing file.");
        var buffer = new byte[entry.SizeBytes];
        stream.Position = entry.DataOffset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0)
                throw new IOException("Unexpected end of audio media.");
            read += count;
        }
        return buffer;
    }

    /// <summary>NVorbis 的 float 采样桥接到 NAudio；reader 归引擎所有。</summary>
    private sealed class NvorbisFloatSampleProvider : ISampleProvider
    {
        private readonly VorbisReader _reader;

        public NvorbisFloatSampleProvider(VorbisReader reader) => _reader = reader;

        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            if (offset != 0)
            {
                // NVorbis 从给定 offset 开始填充；保持 buffer 切片语义。
                return _reader.ReadSamples(buffer, offset, count);
            }
            return _reader.ReadSamples(buffer, 0, count);
        }
    }
}
