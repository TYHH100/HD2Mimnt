using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class ModHashServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Hash-{Guid.NewGuid():N}");

    [Fact]
    public async Task CalculateAsync_ShouldIgnoreRootCreationOrderAndTimestampsButIncludePathsAndFullContent()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(Path.Combine(first, "选项"));
        Directory.CreateDirectory(Path.Combine(second, "选项"));
        var content = Enumerable.Repeat((byte)1, 200000).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(first, "选项", "body.patch"), content);
        await File.WriteAllTextAsync(Path.Combine(first, "extra.patch"), "abc");
        await File.WriteAllTextAsync(Path.Combine(second, "extra.patch"), "abc");
        await File.WriteAllBytesAsync(Path.Combine(second, "选项", "body.patch"), content);
        File.SetLastWriteTimeUtc(Path.Combine(second, "extra.patch"), DateTime.UtcNow.AddYears(-1));
        var original = await ModHashService.CalculateAsync(first);
        Assert.Matches("^[0-9a-f]{64}$", original);
        Assert.Equal(original, await ModHashService.CalculateAsync(second));
        // 同名同长度，仅修改文件中部，快速首尾指纹无法识别这个变化。
        content[100000] = 2;
        await File.WriteAllBytesAsync(Path.Combine(second, "选项", "body.patch"), content);
        Assert.NotEqual(original, await ModHashService.CalculateAsync(second));
        content[100000] = 1;
        await File.WriteAllBytesAsync(Path.Combine(second, "选项", "body.patch"), content);
        File.Move(Path.Combine(second, "extra.patch"), Path.Combine(second, "renamed.patch"));
        Assert.NotEqual(original, await ModHashService.CalculateAsync(second));
    }

    [Fact]
    public async Task CalculateAsync_EmptyDirectory_ShouldReturnStableHashAndDistinguishEmptyFile()
    {
        Directory.CreateDirectory(_root);
        var hash = await ModHashService.CalculateAsync(_root);
        Assert.Equal(hash, await ModHashService.CalculateAsync(_root));
        await File.WriteAllTextAsync(Path.Combine(_root, "empty.patch"), "");
        Assert.NotEqual(hash, await ModHashService.CalculateAsync(_root));
    }

    [Fact]
    public async Task CalculateAsync_MissingDirectoryOrCancellation_ShouldFail()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ModHashService.CalculateAsync(_root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModHashService.CalculateAsync(_root, cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
