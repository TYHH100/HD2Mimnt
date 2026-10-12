using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Downloads;

/// <summary>Bounded concurrent HTTP range downloads backed by a temporary file.</summary>
public sealed class HttpDownloadService : IHttpDownloadService
{
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
        _resumeDirectory = resumeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2Mimnt.dev", "Downloads");
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
        File.Move(temporary, target, overwrite: true);
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
                await file.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                succeeded = true;
            }
            catch (InvalidDataException)
            {
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

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
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
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
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
        options.Validate();
    }

    private sealed class RangeIgnoredException : Exception { }

    private sealed class DownloadReporter(IProgress<DownloadProgress>? progress, long total)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _downloaded;
        private long _restored;
        private readonly Dictionary<long, long> _segments = [];
        public long TotalBytes { get; set; } = total;

        public void Reset()
        {
            lock (_gate)
            {
                _downloaded = 0;
                _restored = 0;
                _segments.Clear();
                _clock.Restart();
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
                if (_restored > 0) Report("正在续传");
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
                Report("下载完成");
            }
        }

        private void Report(string message) => progress?.Report(new DownloadProgress
        {
            TotalBytes = TotalBytes,
            DownloadedBytes = _downloaded,
            BytesPerSecond = (long)((_downloaded - _restored) / Math.Max(_clock.Elapsed.TotalSeconds, 0.001)),
            StatusMessage = message
        });
    }
}
