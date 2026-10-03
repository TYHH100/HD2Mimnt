using HD2Mimnt.UI.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class FileLogServiceTests
{
    [Fact]
    public async Task ReadRecentAsync_ReturnsBoundedTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileLogService(root);
            await File.WriteAllLinesAsync(service.LogFilePath, Enumerable.Range(1, 10).Select(i => $"line-{i}"));

            var lines = await service.ReadRecentAsync(3);

            Assert.Equal(new[] { "line-8", "line-9", "line-10" }, lines);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportAsync_CopiesLogWhileSourceIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileLogService(root);
            await File.WriteAllTextAsync(service.LogFilePath, "diagnostic");
            var destination = Path.Combine(root, "export.log");
            await service.ExportAsync(destination);

            Assert.Equal("diagnostic", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
