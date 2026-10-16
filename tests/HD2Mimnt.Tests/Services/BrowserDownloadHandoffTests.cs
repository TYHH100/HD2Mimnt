using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Tests.Services;

public sealed class BrowserDownloadHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Browser-" + Guid.NewGuid().ToString("N"));
    private readonly PlatformModFile _file = new() { FileName = "armor.zip", Size = 4 };
    public BrowserDownloadHandoffTests() => Directory.CreateDirectory(_root);
    private BrowserDownloadHandoffService Service(int timeout = 3000) =>
        new(_root, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(timeout));
    private string FilePath(string name) => Path.Combine(_root, name);
    private async Task<string> WriteAsync(string name, string content = "test")
    { var path = FilePath(name); await File.WriteAllTextAsync(path, content); return path; }

    [Fact]
    public async Task Automatic_ShouldIgnoreExistingAndUnrelatedFilesAndAcceptBrowserNumberSuffix()
    {
        await WriteAsync("armor.zip");
        using var session = await Service().StartAsync(_file, default);
        await WriteAsync("other.zip");
        var expected = await WriteAsync("armor (1).zip");
        Assert.Equal(expected, await session.WaitForFileAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task Automatic_ShouldWaitForPartialMarkerAndFileLockToClear()
    {
        using var session = await Service().StartAsync(_file, default);
        var path = await WriteAsync("armor.zip");
        var partial = await WriteAsync("armor.zip.crdownload");
        var wait = session.WaitForFileAsync(default);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        using (var writing = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            File.Delete(partial);
            await Task.Delay(100);
            Assert.False(wait.IsCompleted);
        }
        Assert.Equal(path, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_ShouldNotGuessWhenSizeIsWrongOrMultipleCandidatesExist(bool ambiguous)
    {
        using var session = await Service(250).StartAsync(_file, default);
        await WriteAsync("armor.zip", ambiguous ? "test" : "incomplete");
        if (ambiguous) await WriteAsync("armor (1).zip");
        await Assert.ThrowsAsync<TimeoutException>(() => session.WaitForFileAsync(default));
    }

    [Fact]
    public async Task Manual_ShouldAcceptRenamedExistingFileAndRejectIncompleteOrNonArchive()
    {
        var path = await WriteAsync("renamed.zip");
        using var session = await Service().StartAsync(_file, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SelectFileAsync(FilePath("missing.txt"), default));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await session.SelectFileAsync(await WriteAsync("incomplete.zip", "x"), default));
        await session.SelectFileAsync(path, default);
        Assert.Equal(path, await session.WaitForFileAsync(default));
    }

    [Fact]
    public async Task UnknownMetadata_ShouldRequireManualSelection()
    {
        var file = new PlatformModFile { FileName = "armor.zip", Size = 0 };
        using var session = await Service().StartAsync(file, default);
        var wait = session.WaitForFileAsync(default);
        var path = await WriteAsync("armor.zip");
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        await session.SelectFileAsync(path, default);
        Assert.Equal(path, await wait);
    }

    [Fact]
    public async Task CancelAndDispose_ShouldStopWaitingAndReleaseGlobalSlot()
    {
        var service = Service();
        using var cancellation = new CancellationTokenSource();
        using (var session = await service.StartAsync(_file, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(_file, default));
            var wait = session.WaitForFileAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        var next = await service.StartAsync(_file, default);
        var nextWait = next.WaitForFileAsync(default);
        next.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextWait);
        using var last = await service.StartAsync(_file, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseFile_ShouldRetainUserArchiveAndReleaseReadLeaseOnSuccessOrFailure(bool fail)
    {
        var path = await WriteAsync("armor.zip");
        async Task Import(string source, CancellationToken token)
        {
            Assert.Equal("test", await File.ReadAllTextAsync(source, token));
            Assert.Throws<IOException>(() => new FileStream(source, FileMode.Open, FileAccess.Write));
            if (fail) throw new InvalidDataException("synthetic import failure");
        }
        if (fail) await Assert.ThrowsAsync<InvalidDataException>(() => BrowserDownloadHandoffService.UseFileAsync(path, _file, Import, default));
        else await BrowserDownloadHandoffService.UseFileAsync(path, _file, Import, default);
        Assert.True(File.Exists(path));
        using var writable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
