using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Downloads;

/// <summary>Bounded concurrent HTTP range downloads backed by a temporary file.</summary>
public sealed class HttpDownloadService : IHttpDownloadService
{
    /// <summary>宿主通常把 HttpClient.Timeout 设为 Infinite；响应头阶段单独设死线防服务器挂起。</summary>
    private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(120);
    /// <summary>响应体连续无字节到达的空闲上限，超时按瞬时故障处理以触发重试。</summary>
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly IDownloadOptionsProvider? _optionsProvider;
    private readonly string _resumeDirectory;
    private readonly string _tempDirectory;

    // The caller owns HttpClient; credentials must be attached to API requests, not its default headers.
    public HttpDownloadService(HttpClient httpClient, IDownloadOptionsProvider? optionsProvider = null,
        string? resumeDirectory = null, string? tempDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _optionsProvider = optionsProvider;
        _resumeDirectory = resumeDirectory ?? Path.Combine(RuntimeDataPaths.GetDefaultApplicationDataPath(), "Downloads");
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
    }

    public async Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        string? resumeKey = null)
    {
        options ??= _optionsProvider is null ? new DownloadOptions() : await _optionsProvider.GetOptionsAsync();
        Validate(uri, options);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = GetIdentity(uri, resumeKey);
        var directory = options.EnableResume ? _resumeDirectory : _tempDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, options.EnableResume ? identity + ".part" : $"HD2Mimnt-download-{Guid.NewGuid():N}.tmp");
        await using var lease = AcquireLease(path);
        await DownloadStoredAsync(uri, path, identity, options, progress, cancellationToken);
        // Successful streams retain the original dispose-to-delete contract.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    public async Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        string? resumeKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        options ??= _optionsProvider is null ? new DownloadOptions() : await _optionsProvider.GetOptionsAsync();
        Validate(uri, options);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        // A sibling file allows publication by rename on the same volume.
        var temporary = target + ".hd2download.part";
        await using var lease = AcquireLease(temporary);
        await DownloadStoredAsync(uri, temporary, GetIdentity(uri, resumeKey), options, progress, cancellationToken);
        try { File.Move(temporary, target, overwrite: options.OverwriteExisting); }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>流式校验文件哈希；不匹配抛 InvalidDataException（上层会删除文件）。</summary>
    private static async Task VerifyHashAsync(FileStream file, string expected, CancellationToken cancellationToken)
    {
        var separator = expected.IndexOf(':');
        var algorithm = expected[..separator];
        var hex = expected[(separator + 1)..];
        file.Position = 0;
        string actual;
        using (var hash = algorithm switch
        {
            "sha256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "md5" => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            _ => throw new InvalidDataException($"不支持的哈希算法: {algorithm}")
        })
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken)) != 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }

        if (!actual.Equals(hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"下载内容校验失败：{algorithm} 不匹配（期望 {hex[..8]}…，实际 {actual[..8]}…）。");
        }
    }

    private static string GetIdentity(Uri uri, string? resumeKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resumeKey ?? uri.AbsoluteUri)));

    private static FileStream AcquireLease(string path) => new(path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private async Task DownloadStoredAsync(Uri uri, string path, string identity, DownloadOptions options,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var context = new ResumeContext(identity, path + ".json", options.EnableResume);
        var succeeded = false;
        try
        {
            await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                await DownloadCoreAsync(uri, file, options, progress, context, cancellationToken);
                if (options.ExpectedHash is { Length: > 0 })
                {
                    await VerifyHashAsync(file, options.ExpectedHash, cancellationToken);
                }
                await file.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                succeeded = true;
            }
            catch (InvalidDataException)
            {
                // 校验失败（哈希不匹配/协议违规）时断点数据同样不可信
                context.Discard();
                throw;
            }
            finally
            {
                if (!succeeded && context.State is not null)
                    await context.State.SaveAsync(file, context.MetadataPath);
            }
        }
        finally
        {
            if (succeeded || !context.CanKeepPartial)
            {
                File.Delete(context.MetadataPath);
                if (!succeeded) File.Delete(path);
            }
        }
    }

    private sealed class ResumeContext(string identity, string metadataPath, bool enabled)
    {
        public string Identity { get; } = identity;
        public string MetadataPath { get; } = metadataPath;
        public DownloadCheckpoint? State { get; set; }
        public bool CanKeepPartial { get; private set; } = enabled && File.Exists(metadataPath);
        public bool Enabled { get; } = enabled;

        public void Discard()
        {
            State = null;
            CanKeepPartial = false;
            File.Delete(MetadataPath);
            File.Delete(MetadataPath + ".tmp");
        }

        public void Keep(DownloadCheckpoint state)
        {
            State = state;
            CanKeepPartial = true;
        }
    }

    private async Task DownloadCoreAsync(Uri uri, FileStream file, DownloadOptions options,
        IProgress<DownloadProgress>? progress, ResumeContext context, CancellationToken cancellationToken)
    {
        using var initial = await SendAsync(uri, null, null, cancellationToken);
        EnsureFullResponse(initial);
        var length = initial.Content.Headers.ContentLength;
        var etag = initial.Headers.ETag is { IsWeak: false } tag ? tag : null;
        var modified = initial.Content.Headers.LastModified;
        var workers = length is > 0
            ? (int)Math.Min(options.MaxParallelSegments, length.Value / options.MinimumSegmentSize) : 1;
        var reporter = new DownloadReporter(progress, length ?? 0);
        // Validators prevent mixing bytes from different revisions of a URL.
        if ((!context.Enabled && workers < 2) || length is not > 0 || !initial.Headers.AcceptRanges.Contains("bytes") ||
            (etag is null && modified is null))
        {
            context.Discard();
            file.SetLength(0);
            file.Position = 0;
            await CopyFullAsync(initial, file, reporter, cancellationToken);
            return;
        }

        initial.Dispose();
        DownloadCheckpoint? state = null;
        if (context.Enabled && File.Exists(context.MetadataPath) && file.Length == length!.Value)
        {
            try
            {
                var json = await File.ReadAllTextAsync(context.MetadataPath, cancellationToken);
                state = JsonSerializer.Deserialize<DownloadCheckpoint>(json);
                if (state is null || !state.Matches(context.Identity, length.Value, etag?.Tag, modified)) state = null;
            }
            catch (JsonException) { /* Interrupted or obsolete checkpoint: restart safely. */ }
        }
        if (state is null)
        {
            context.Discard();
            file.SetLength(0);
            file.SetLength(length!.Value);
            workers = Math.Max(1, workers);
            state = new DownloadCheckpoint
            {
                Identity = context.Identity, Length = length.Value, ETag = etag?.Tag, LastModified = modified,
                Segments = Enumerable.Range(0, workers).Select(index => new DownloadSegment
                {
                    Start = index * (length.Value / workers),
                    End = index == workers - 1 ? length.Value - 1 : (index + 1) * (length.Value / workers) - 1
                }).ToList()
            };
        }
        if (context.Enabled)
        {
            context.Keep(state);
            await state.SaveAsync(file, context.MetadataPath);
        }
        reporter.Restore(state.Segments);
        reporter.SetLayout(state.Segments);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ignoredRange = false;
        using var slots = new SemaphoreSlim(options.MaxParallelSegments);
        var tasks = state.Segments.Select(async segment =>
        {
            await slots.WaitAsync(linked.Token);
            try
            {
                await DownloadSegmentAsync(uri, file, segment, state, etag, modified,
                    options, reporter, context, linked.Token);
            }
            catch (RangeIgnoredException)
            {
                Volatile.Write(ref ignoredRange, true);
                await linked.CancelAsync();
                throw;
            }
            catch
            {
                await linked.CancelAsync();
                throw;
            }
            finally { slots.Release(); }
        }).ToArray();
        try
        {
            // Always join all writers before disposing the file or restarting a full download.
            await Task.WhenAll(tasks);
        }
        catch when (Volatile.Read(ref ignoredRange) && !cancellationToken.IsCancellationRequested)
        {
            context.Discard();
            file.SetLength(0);
            file.Position = 0;
            reporter.Reset();
            using var fallback = await SendAsync(uri, null, null, cancellationToken);
            EnsureFullResponse(fallback);
            reporter.TotalBytes = fallback.Content.Headers.ContentLength ?? 0;
            await CopyFullAsync(fallback, file, reporter, cancellationToken);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        reporter.Complete(length.Value);
    }

    private async Task DownloadSegmentAsync(Uri uri, FileStream file, DownloadSegment segment, DownloadCheckpoint state,
        EntityTagHeaderValue? etag, DateTimeOffset? modified, DownloadOptions options,
        DownloadReporter reporter, ResumeContext context, CancellationToken cancellationToken)
    {
        var end = segment.End;
        for (var attempt = 0; ; attempt++)
        {
            var start = segment.Start + segment.Downloaded;
            if (start > end) return;
            try
            {
                using var response = await SendAsync(uri, new RangeHeaderValue(start, end),
                    etag is not null ? new RangeConditionHeaderValue(etag) : new RangeConditionHeaderValue(modified!.Value),
                    cancellationToken);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new RangeIgnoredException();
                response.EnsureSuccessStatusCode();
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null ||
                    range.Unit != "bytes" || range.From != start || range.To != end || range.Length != state.Length ||
                    (response.Content.Headers.ContentLength is { } count && count != end - start + 1) ||
                    (etag is not null && response.Headers.ETag is { } returnedTag && !etag.Equals(returnedTag)) ||
                    (etag is null && response.Content.Headers.LastModified is { } returnedDate && returnedDate != modified) ||
                    response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("服务器返回的分段范围、长度或文件版本不匹配。");

                await using var source = new IdleWatchdogStream(
                    await response.Content.ReadAsStreamAsync(cancellationToken), ReadIdleTimeout);
                var buffer = new byte[81920];
                var offset = start;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (read > end - offset + 1)
                        throw new InvalidDataException("分段响应包含超出请求范围的数据。");
                    await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.AsMemory(0, read), offset, cancellationToken);
                    offset += read;
                    var checkpointDue = state.Advance(segment, offset);
                    reporter.SetSegment(segment.Start, segment.Downloaded);
                    if (context.Enabled && checkpointDue) await state.SaveAsync(file, context.MetadataPath);
                }
                if (offset != end + 1) throw new IOException("分段下载提前结束。");
                return;
            }
            catch (Exception ex) when (attempt < options.MaxRetries && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, RangeHeaderValue? range,
        RangeConditionHeaderValue? condition, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.Range = range;
        request.Headers.IfRange = condition;
        // HttpClient.Timeout 由宿主设为 Infinite（分段响应体不能被总时长截断），
        // 响应头阶段单独设死线：连接建立后服务器不发头时按瞬时故障重试而不是永久挂起。
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(ResponseHeadersTimeout);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("下载服务器长时间未返回响应头，已中止本次请求。");
        }
    }

    private static void EnsureFullResponse(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("下载需要完整的未压缩 HTTP 200 响应。");
    }

    private static async Task CopyFullAsync(HttpResponseMessage response, FileStream file,
        DownloadReporter reporter, CancellationToken cancellationToken)
    {
        await using var source = new IdleWatchdogStream(
            await response.Content.ReadAsStreamAsync(cancellationToken), ReadIdleTimeout);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            received += read;
            if (response.Content.Headers.ContentLength is { } length && received > length)
                throw new InvalidDataException("下载响应超出声明的文件长度。");
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reporter.Add(read);
        }
        if (response.Content.Headers.ContentLength is { } expected && received != expected)
            throw new IOException("文件下载提前结束。");
        reporter.Complete(received);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)status >= 500,
        IOException when ex is not InvalidDataException => true,
        _ => false
    };

    private static void Validate(Uri uri, DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("下载地址必须是 HTTP 或 HTTPS 绝对地址。", nameof(uri));
        if (uri.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureHttp)
            throw new ArgumentException("默认仅允许 HTTPS 下载；如确需明文 http://，请设置 DownloadOptions.AllowInsecureHttp。", nameof(uri));
        options.Validate();
    }

    private sealed class RangeIgnoredException : Exception { }

    /// <summary>
    /// 响应体读取空闲看门狗：HttpClient.Timeout 为 Infinite 时，连接建立后服务器停发数据
    /// （不发 FIN、不限速挂起）不会触发任何异常。包装器在 <paramref name="idleTimeout"/>
    /// 内没有字节到达时抛 IOException——在 <see cref="IsTransient"/> 视角下属于可重试的瞬时故障。
    /// </summary>
    private sealed class IdleWatchdogStream(Stream inner, TimeSpan idleTimeout) : Stream
    {
        private readonly CancellationTokenSource _idle = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _idle.Token);
            int read;
            try
            {
                _idle.CancelAfter(idleTimeout);
                read = await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _idle.IsCancellationRequested)
            {
                throw new IOException("下载空闲超时：服务器长时间未继续发送数据。");
            }

            if (read != 0)
            {
                _idle.CancelAfter(idleTimeout);
            }
            return read;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _idle.Dispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DownloadReporter(IProgress<DownloadProgress>? progress, long total)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _downloaded;
        private long _restored;
        private readonly Dictionary<long, long> _segments = [];
        private List<(long Start, long End)>? _layout;
        public long TotalBytes { get; set; } = total;

        public void Reset()
        {
            lock (_gate)
            {
                _downloaded = 0;
                _restored = 0;
                _segments.Clear();
                _layout = null;
                _clock.Restart();
                _lastReported = TimeSpan.Zero;
            }
        }

        public void SetLayout(IReadOnlyList<DownloadSegment> segments)
        {
            lock (_gate)
            {
                _layout = segments.Select(s => (s.Start, s.End)).ToList();
                if (_restored > 0) Report("正在续传", force: true);
            }
        }

        public void Restore(IEnumerable<DownloadSegment> segments)
        {
            lock (_gate)
            {
                foreach (var segment in segments)
                {
                    _segments[segment.Start] = segment.Downloaded;
                    _downloaded += segment.Downloaded;
                }
                _restored = _downloaded;
                _clock.Restart();
                if (_restored > 0) Report("正在续传", force: true);
            }
        }

        // Keep each segment's highest offset, so retrying bytes does not inflate progress.
        public void SetSegment(long start, long received)
        {
            lock (_gate)
            {
                var previous = _segments.GetValueOrDefault(start);
                if (received <= previous) return;
                _segments[start] = received;
                _downloaded += received - previous;
                Report("正在下载");
            }
        }

        public void Add(long bytes)
        {
            lock (_gate)
            {
                _downloaded += bytes;
                Report("正在下载");
            }
        }

        public void Complete(long bytes)
        {
            lock (_gate)
            {
                TotalBytes = bytes;
                Report("下载完成", force: true);
            }
        }

        /// <summary>
        /// 100ms 时间节流：主循环每 80KB 就调一次进度，千兆带宽下每秒上万次回调
        /// 会灌满 UI 消息队列（消费端每条都 Dispatcher.Post）。节流只影响上报频率，
        /// 累计值始终是最新的；终态与低频状态消息用 force 直通，
        /// 首条必须直通——调用方可能依赖第一份进度立即到达（如以进度触发取消），
        /// 与 Worker 进度“首条必须送达”的约定一致。
        /// </summary>
        private static readonly TimeSpan MinReportInterval = TimeSpan.FromMilliseconds(100);
        private TimeSpan _lastReported;
        private bool _reportedAny;

        private void Report(string message, bool force = false)
        {
            lock (_gate)
            {
                var elapsed = _clock.Elapsed;
                if (!force && _reportedAny && elapsed - _lastReported < MinReportInterval) return;
                _reportedAny = true;
                _lastReported = elapsed;

                IReadOnlyList<DownloadSegmentProgress>? segments = null;
                if (_layout is { } layout)
                {
                    var snapshot = new DownloadSegmentProgress[layout.Count];
                    for (var index = 0; index < layout.Count; index++)
                    {
                        var (start, end) = layout[index];
                        snapshot[index] = new DownloadSegmentProgress(start, end, _segments.GetValueOrDefault(start));
                    }
                    segments = snapshot;
                }
                progress?.Report(new DownloadProgress
                {
                    TotalBytes = TotalBytes,
                    DownloadedBytes = _downloaded,
                    BytesPerSecond = (long)((_downloaded - _restored) / Math.Max(_clock.Elapsed.TotalSeconds, 0.001)),
                    StatusMessage = message,
                    Segments = segments
                });
            }
        }
    }
}
