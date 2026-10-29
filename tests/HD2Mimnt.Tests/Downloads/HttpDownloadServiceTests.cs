using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Downloads;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Downloads;

public sealed class HttpDownloadServiceTests : IDisposable
{
    private static readonly Uri Address = new("https://cdn.example.com/mod.zip?signature=test");
    private static readonly DownloadOptions Options = new()
    {
        MaxParallelSegments = 4, MinimumSegmentSize = 1024, RetryDelay = TimeSpan.Zero, EnableResume = false
    };
    private readonly byte[] _data = Enumerable.Range(0, 257 * 1024 + 13).Select(i => (byte)(i % 251)).ToArray();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-download-tests-{Guid.NewGuid():N}");

    public HttpDownloadServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task DownloadAsync_Ranges_Should_Overlap_And_Assemble_Exact_Bytes()
    {
        var started = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ranges = new ConcurrentBag<(long Start, long End)>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("identity", request.Headers.AcceptEncoding.Single().Value);
            if (request.Headers.Range is null) return Full();
            Assert.Equal("\"revision-1\"", request.Headers.IfRange!.EntityTag!.Tag);
            var range = request.Headers.Range.Ranges.Single();
            ranges.Add((range.From!.Value, range.To!.Value));
            if (Interlocked.Increment(ref started) == 4) gate.SetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return Segment(request);
        }));
        var progress = new RecordingProgress();
        var service = new HttpDownloadService(client);
        var stream = (FileStream)await service.DownloadAsync(Address, Options, progress);
        var temporary = stream.Name;
        try
        {
            Assert.Equal(0, stream.Position);
            Assert.Equal(_data.Length, stream.Length);
            using var result = new MemoryStream();
            await stream.CopyToAsync(result);
            Assert.Equal(_data, result.ToArray());
            Assert.Equal(4, ranges.Count);
            long next = 0;
            foreach (var range in ranges.OrderBy(r => r.Start))
            {
                Assert.Equal(next, range.Start);
                next = range.End + 1;
            }
            Assert.Equal(_data.Length, next);
            Assert.Equal(_data.Length, progress.Values[^1].DownloadedBytes);
            Assert.Equal(100, progress.Values[^1].Percentage);
            Assert.Equal("下载完成", progress.Values[^1].StatusMessage);
            Assert.Equal(progress.Values.Select(p => p.DownloadedBytes).Order(),
                progress.Values.Select(p => p.DownloadedBytes));
        }
        finally { await stream.DisposeAsync(); }
        Assert.False(File.Exists(temporary));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task DownloadAsync_Unsupported_Metadata_Should_Use_One_Connection(bool ranges, bool validator, bool length)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Null(request.Headers.Range);
            var response = Full();
            if (!ranges) response.Headers.AcceptRanges.Clear();
            if (!validator) response.Headers.ETag = null;
            if (!length)
            {
                response.Content.Dispose();
                response.Content = new UnknownLengthContent(_data);
            }
            return Task.FromResult(response);
        }));
        var progress = new RecordingProgress();
        await using var stream = await new HttpDownloadService(client).DownloadAsync(Address, Options, progress);
        Assert.Equal(1, requests);
        Assert.Equal(_data.Length, stream.Length);
        Assert.Equal(100, progress.Values[^1].Percentage);
    }

    [Fact]
    public async Task DownloadAsync_Single_Connection_Option_Should_Not_Send_Ranges()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Task.FromResult(Full());
        }));
        await using var stream = await new HttpDownloadService(client).DownloadAsync(Address,
            new DownloadOptions { MaxParallelSegments = 1, EnableResume = false });
        Assert.Equal(_data.Length, stream.Length);
    }

    [Fact]
    public async Task DownloadAsync_LastModified_Should_Be_Used_When_Strong_Etag_Is_Absent()
    {
        var date = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var response = request.Headers.Range is null ? Full() : Segment(request);
            response.Headers.ETag = new EntityTagHeaderValue("\"weak\"", isWeak: true);
            response.Content.Headers.LastModified = date;
            if (request.Headers.Range is not null) Assert.Equal(date, request.Headers.IfRange!.Date);
            return Task.FromResult(response);
        }));
        await using var stream = await new HttpDownloadService(client).DownloadAsync(Address, Options);
        Assert.Equal(_data.Length, stream.Length);
    }

    [Fact]
    public async Task DownloadToFileAsync_Ignored_Range_Should_Restart_Full_Download()
    {
        var fullRequests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) fullRequests++;
            return Task.FromResult(Full()); // Advertises ranges but ignores Range.
        }));
        var path = Path.Combine(_directory, "mod.zip");
        await new HttpDownloadService(client).DownloadToFileAsync(Address, path, Options);
        Assert.Equal(2, fullRequests);
        Assert.Equal(_data, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("etag")]
    [InlineData("overflow")]
    public async Task DownloadToFileAsync_Invalid_Response_Should_Preserve_Target_And_Clean_Partial(string error)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) return Task.FromResult(Full());
            var response = Segment(request);
            if (error == "range") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, _data.Length);
            if (error == "etag") response.Headers.ETag = new EntityTagHeaderValue("\"revision-2\"");
            if (error == "overflow") response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        }));
        var path = Path.Combine(_directory, "existing.zip");
        await File.WriteAllTextAsync(path, "original");
        await Assert.ThrowsAsync<InvalidDataException>(() => new HttpDownloadService(client)
            .DownloadToFileAsync(Address, path, Options));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_Transient_Segment_Failure_Should_Retry_Without_Double_Counting(bool truncated)
    {
        var attempts = new ConcurrentDictionary<long, int>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) return Task.FromResult(Full());
            var range = request.Headers.Range.Ranges.Single();
            var segmentStart = range.From!.Value / (_data.Length / 4) * (_data.Length / 4);
            var attempt = attempts.AddOrUpdate(segmentStart, 1, (_, old) => old + 1);
            if (attempt > 1) return Task.FromResult(Segment(request));
            if (!truncated) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var response = Segment(request);
            var headers = response.Content.Headers.ContentRange;
            response.Content.Dispose();
            response.Content = new ByteArrayContent([_data[(int)range.From.Value]]);
            response.Content.Headers.ContentRange = headers;
            response.Content.Headers.ContentLength = range.To!.Value - range.From.Value + 1;
            return Task.FromResult(response);
        }));
        var progress = new RecordingProgress();
        await using var stream = await new HttpDownloadService(client).DownloadAsync(Address, Options, progress);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(_data, bytes.ToArray());
        Assert.All(attempts.Values, count => Assert.Equal(2, count));
        Assert.All(progress.Values, p => Assert.InRange(p.DownloadedBytes, 0, _data.Length));
        Assert.Equal(_data.Length, progress.Values[^1].DownloadedBytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task DownloadToFileAsync_Http_Failure_Should_Respect_Retry_Limit(HttpStatusCode status, int count)
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) return Task.FromResult(Full());
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        var path = Path.Combine(_directory, "failed.zip");
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpDownloadService(client)
            .DownloadToFileAsync(Address, path, new DownloadOptions
            {
                MaxParallelSegments = 2, MinimumSegmentSize = 1024, RetryDelay = TimeSpan.Zero, EnableResume = false
            }));
        // The first synchronous failure cancels peer requests before they start.
        Assert.Equal(count, attempts);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadToFileAsync_Cancellation_Should_Join_Writers_And_Preserve_Target()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Headers.Range is null) return Full();
            Interlocked.Increment(ref active);
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref active); }
            return Segment(request);
        }));
        var path = Path.Combine(_directory, "existing.zip");
        await File.WriteAllTextAsync(path, "original");
        var download = new HttpDownloadService(client).DownloadToFileAsync(Address, path, Options,
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(0, active);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadToFileAsync_Truncated_Full_Response_Should_Not_Publish()
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
            response.Content.Headers.ContentLength = 3;
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<IOException>(() => new HttpDownloadService(client)
            .DownloadToFileAsync(Address, Path.Combine(_directory, "truncated.zip")));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadAsync_PreCanceled_Should_Not_Send_Request()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpDownloadService(client)
            .DownloadAsync(Address, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void DownloadOptions_MaxParallelSegments_Should_Allow_Up_To_256()
    {
        new DownloadOptions { MaxParallelSegments = 256 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownloadOptions { MaxParallelSegments = 257 }.Validate());
    }

    [Fact]
    public async Task DownloadAsync_Invalid_Options_Should_Fail_Before_Network_IO()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        var service = new HttpDownloadService(client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadAsync(new Uri("file:///mod.zip")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DownloadAsync(Address,
            new DownloadOptions { MaxParallelSegments = 257 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DownloadAsync(Address,
            new DownloadOptions { MinimumSegmentSize = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DownloadAsync(Address,
            new DownloadOptions { MaxRetries = -1 }));
    }

    [Fact]
    public async Task DownloadToFileAsync_ExpectedHash_Match_Should_Publish()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_data)).ToLowerInvariant();
        var path = Path.Combine(_directory, "verified.zip");
        var options = new DownloadOptions
        {
            MaxParallelSegments = 4, MinimumSegmentSize = 1024, RetryDelay = TimeSpan.Zero,
            EnableResume = false, ExpectedHash = "sha256:" + sha256
        };
        await new HttpDownloadService(client).DownloadToFileAsync(Address, path, options);
        Assert.Equal(_data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadToFileAsync_ExpectedHash_Mismatch_Should_Delete_And_Throw()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var wrong = new string('a', 64);
        var path = Path.Combine(_directory, "tampered.zip");
        var options = new DownloadOptions
        {
            MaxParallelSegments = 1, MinimumSegmentSize = 1024, RetryDelay = TimeSpan.Zero,
            EnableResume = true, ExpectedHash = "sha256:" + wrong
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpDownloadService(client).DownloadToFileAsync(Address, path, options));
        // 目标与暂存/断点文件都不得残留
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadAsync_InsecureHttp_RejectedUnlessExplicitlyAllowed()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        var insecure = new Uri("http://cdn.example.com/mod.zip");
        // 默认拒绝明文 http
        await Assert.ThrowsAsync<ArgumentException>(() => new HttpDownloadService(client).DownloadAsync(insecure, Options));
        // 用户显式选择时放行（此 handler 会在收到请求后失败，仅验证不再被 Validate 拦截）
        var allowed = new DownloadOptions { MaxParallelSegments = 1, MinimumSegmentSize = 1024, RetryDelay = TimeSpan.Zero, EnableResume = false, AllowInsecureHttp = true };
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => new HttpDownloadService(client).DownloadAsync(insecure, allowed));
    }

    [Fact]
    public async Task DownloadAsync_Invalid_ExpectedHash_Format_Should_Fail_Before_Network_IO()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        await Assert.ThrowsAsync<ArgumentException>(() => new HttpDownloadService(client).DownloadAsync(Address,
            new DownloadOptions { ExpectedHash = "sha1:" + new string('a', 40) }));
        await Assert.ThrowsAsync<ArgumentException>(() => new HttpDownloadService(client).DownloadAsync(Address,
            new DownloadOptions { ExpectedHash = "sha256:not-hex" }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task DownloadToFileAsync_Interrupted_Should_Resume_With_New_Service_And_Skip_Saved_Bytes(int parallelism)
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var options = new DownloadOptions
        {
            MaxParallelSegments = parallelism, MinimumSegmentSize = 1024, EnableResume = true
        };
        var target = Path.Combine(_directory, "resume.zip");
        await File.WriteAllTextAsync(target, "original");
        await InterruptAsync(new HttpDownloadService(client), target, options);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(target + ".hd2download.part"));
        Assert.True(File.Exists(target + ".hd2download.part.json"));
        var saved = await File.ReadAllTextAsync(target + ".hd2download.part.json");
        Assert.DoesNotContain("signature=test", saved);
        var starts = new ConcurrentBag<long>();
        using var resumedClient = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) return Task.FromResult(Full());
            starts.Add(request.Headers.Range.Ranges.Single().From!.Value);
            return Task.FromResult(Segment(request));
        }));
        var progress = new RecordingProgress();
        await new HttpDownloadService(resumedClient).DownloadToFileAsync(Address, target, options, progress);
        Assert.All(starts, start => Assert.True(start > 0));
        Assert.Equal(_data, await File.ReadAllBytesAsync(target));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.True(progress.Values[0].DownloadedBytes > 0);
        Assert.Equal(0, progress.Values[0].BytesPerSecond);
    }

    [Fact]
    public async Task DownloadAsync_Changed_Signed_Url_Should_Resume_By_Stable_File_Key()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        using var cancellation = new CancellationTokenSource();
        var options = new DownloadOptions { MaxParallelSegments = 1, EnableResume = true };
        var progress = new CallbackProgress(p => { if (p.DownloadedBytes > 0) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpDownloadService(client, resumeDirectory: _directory)
            .DownloadAsync(Address, options, progress, cancellation.Token, resumeKey: "platform:version:123"));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
        var start = 0L;
        using var resumedClient = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) return Task.FromResult(Full());
            start = request.Headers.Range.Ranges.Single().From!.Value;
            return Task.FromResult(Segment(request));
        }));
        var resumed = await new HttpDownloadService(resumedClient, resumeDirectory: _directory)
            .DownloadAsync(new Uri("https://cdn.example.com/mod.zip?signature=refreshed"), options,
                resumeKey: "platform:version:123");
        await using (resumed)
        {
            Assert.True(start > 0);
            using var result = new MemoryStream();
            await resumed.CopyToAsync(result);
            Assert.Equal(_data, result.ToArray());
        }
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("length")]
    [InlineData("unsupported")]
    [InlineData("corrupt")]
    [InlineData("missing-part")]
    public async Task DownloadToFileAsync_Incompatible_Checkpoint_Should_Restart(string change)
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var options = new DownloadOptions { MaxParallelSegments = 1 };
        var target = Path.Combine(_directory, "changed.zip");
        await InterruptAsync(new HttpDownloadService(client), target, options);
        if (change == "corrupt") await File.WriteAllTextAsync(target + ".hd2download.part.json", "{invalid");
        if (change == "missing-part") File.Delete(target + ".hd2download.part");
        var bytes = change == "length" ? _data[..^5] : _data;
        var ranges = new List<long>();
        using var nextClient = new HttpClient(new Handler((request, _) =>
        {
            HttpResponseMessage response;
            if (request.Headers.Range is null)
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            else
            {
                var range = request.Headers.Range.Ranges.Single();
                ranges.Add(range.From!.Value);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes[(int)range.From.Value..((int)range.To!.Value + 1)])
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From.Value, range.To.Value, bytes.Length);
            }
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue(change == "etag" ? "\"revision-2\"" : "\"revision-1\"");
            if (change == "unsupported") response.Headers.AcceptRanges.Clear();
            return Task.FromResult(response);
        }));
        await new HttpDownloadService(nextClient).DownloadToFileAsync(Address, target, options);
        if (change == "unsupported") Assert.Empty(ranges);
        else Assert.Equal(0, Assert.Single(ranges));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadToFileAsync_Disabling_Resume_Should_Discard_Old_Checkpoint()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var target = Path.Combine(_directory, "disabled.zip");
        await InterruptAsync(new HttpDownloadService(client), target, new DownloadOptions { MaxParallelSegments = 1 });
        await new HttpDownloadService(client).DownloadToFileAsync(Address, target,
            new DownloadOptions { MaxParallelSegments = 1, EnableResume = false });
        Assert.Equal(_data, await File.ReadAllBytesAsync(target));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadToFileAsync_Second_Writer_Should_Not_Access_Active_Checkpoint()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Headers.Range is null) return Full();
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Segment(request);
        }));
        var target = Path.Combine(_directory, "busy.zip");
        var service = new HttpDownloadService(client);
        var running = service.DownloadToFileAsync(Address, target, new DownloadOptions { MaxParallelSegments = 1 },
            cancellationToken: cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => service.DownloadToFileAsync(Address, target));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task DownloadToFileAsync_Resume_With_Lower_Parallelism_Should_Respect_New_Limit()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var target = Path.Combine(_directory, "limit.zip");
        await InterruptAsync(new HttpDownloadService(client), target,
            new DownloadOptions { MaxParallelSegments = 4, MinimumSegmentSize = 1024 });
        var active = 0;
        using var nextClient = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Headers.Range is null) return Full();
            Assert.Equal(1, Interlocked.Increment(ref active));
            try { await Task.Delay(5, token); return Segment(request); }
            finally { Interlocked.Decrement(ref active); }
        }));
        await new HttpDownloadService(nextClient).DownloadToFileAsync(Address, target,
            new DownloadOptions { MaxParallelSegments = 1 });
        Assert.Equal(_data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DownloadAsync_Saved_Settings_Should_Change_Actual_Concurrency_On_Next_Transfer()
    {
        var settings = new DownloadSettingsService(new SettingsService(_directory));
        var data = new byte[4 * 1024 * 1024];
        var starts = new List<long>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            HttpResponseMessage response;
            if (request.Headers.Range is null)
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            else
            {
                var range = request.Headers.Range.Ranges.Single();
                starts.Add(range.From!.Value);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(data[(int)range.From.Value..((int)range.To!.Value + 1)])
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From.Value, range.To.Value, data.Length);
            }
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue("\"revision\"");
            return Task.FromResult(response);
        }));
        var downloader = new HttpDownloadService(client, settings, _directory);
        await settings.SaveAsync(new DownloadOptions
        {
            MaxParallelSegments = 2, MinimumSegmentSize = 1024 * 1024, EnableResume = false
        });
        await using (var stream = await downloader.DownloadAsync(Address)) Assert.Equal(data.Length, stream.Length);
        Assert.Equal(new long[] { 0, data.Length / 2 }, starts);
        starts.Clear();
        await settings.SaveAsync(new DownloadOptions { MaxParallelSegments = 1, EnableResume = false });
        await using (var stream = await downloader.DownloadAsync(Address)) Assert.Equal(data.Length, stream.Length);
        Assert.Empty(starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadToFileAsync_LastModified_Should_Validate_Resume(bool changed)
    {
        var modified = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var starts = new List<long>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var response = request.Headers.Range is null ? Full() : Segment(request);
            response.Headers.ETag = null;
            response.Content.Headers.LastModified = modified;
            if (request.Headers.Range is not null)
            {
                Assert.Equal(modified, request.Headers.IfRange!.Date);
                starts.Add(request.Headers.Range.Ranges.Single().From!.Value);
            }
            return Task.FromResult(response);
        }));
        var target = Path.Combine(_directory, "dated.zip");
        var options = new DownloadOptions { MaxParallelSegments = 1 };
        await InterruptAsync(new HttpDownloadService(client), target, options);
        starts.Clear();
        if (changed) modified = modified.AddDays(1);
        await new HttpDownloadService(client).DownloadToFileAsync(Address, target, options);
        var start = Assert.Single(starts);
        if (changed) Assert.Equal(0, start);
        else Assert.True(start > 0);
        Assert.Equal(_data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DownloadToFileAsync_Expired_Link_Should_Preserve_Checkpoint_For_Retry()
    {
        using var client = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Headers.Range is null ? Full() : Segment(request))));
        var target = Path.Combine(_directory, "expired.zip");
        var options = new DownloadOptions { MaxParallelSegments = 1 };
        await InterruptAsync(new HttpDownloadService(client), target, options);
        var previous = await File.ReadAllTextAsync(target + ".hd2download.part.json");
        using var denied = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpDownloadService(denied)
            .DownloadToFileAsync(Address, target, options));
        Assert.Equal(previous, await File.ReadAllTextAsync(target + ".hd2download.part.json"));
        await new HttpDownloadService(client).DownloadToFileAsync(Address, target, options);
        Assert.Equal(_data, await File.ReadAllBytesAsync(target));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DownloadToFileAsync_NoOverwrite_PreservesFileCreatedDuringDownloadAndCleansTemporary()
    {
        var target = Path.Combine(_directory, "existing.zip");
        using var client = new HttpClient(new Handler(async (_, _) =>
        {
            await File.WriteAllTextAsync(target, "created by another operation");
            return Full();
        }));
        await Assert.ThrowsAsync<IOException>(() => new HttpDownloadService(client).DownloadToFileAsync(Address, target,
            new DownloadOptions { EnableResume = false, MaxParallelSegments = 1, OverwriteExisting = false }));
        Assert.Equal("created by another operation", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(_directory));
    }

    private async Task InterruptAsync(HttpDownloadService service, string target, DownloadOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(p => { if (p.DownloadedBytes > 0) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadToFileAsync(Address, target, options, progress, cancellation.Token));
    }

    private sealed class CallbackProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => callback(value);
    }

    private HttpResponseMessage Full()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_data) };
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
        return response;
    }

    private HttpResponseMessage Segment(HttpRequestMessage request)
    {
        var range = request.Headers.Range!.Ranges.Single();
        var start = (int)range.From!.Value;
        var end = (int)range.To!.Value;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(_data[start..(end + 1)])
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _data.Length);
        response.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Values { get; } = [];
        public void Report(DownloadProgress value) => Values.Add(value);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }
}
