using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

public class DownloadManagerServiceTests : IAsyncLifetime
{
    private readonly Mock<IPlatformRegistry> _mockRegistry;
    private readonly Mock<IModPlatform> _mockPlatform;
    private readonly DownloadManagerService _service;
    private readonly string _tempDir;

    public DownloadManagerServiceTests()
    {
        _mockRegistry = new Mock<IPlatformRegistry>();
        _mockPlatform = new Mock<IModPlatform>();
        _service = new DownloadManagerService(_mockRegistry.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Constructor_Should_Throw_When_Registry_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new DownloadManagerService(null!));
    }

    [Fact]
    public void EnqueueDownload_Should_Create_Task()
    {
        // Arrange
        SetupMockPlatform();

        // Act
        var taskId = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test.zip"));

        // Assert
        Assert.NotEqual(Guid.Empty, taskId);
        var task = _service.GetDownloadTask(taskId);
        Assert.NotNull(task);
        Assert.Equal("test", task.PlatformId);
        Assert.Equal("mod1", task.ModId);
    }

    [Fact]
    public void EnqueueDownload_Should_Throw_When_Platform_Not_Found()
    {
        // Arrange
        _mockRegistry.Setup(r => r.GetPlatform(It.IsAny<string>())).Returns((IModPlatform?)null);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _service.EnqueueDownload("unknown", "mod1", "path"));
    }

    [Fact]
    public async Task EnqueueDownload_Should_Start_Download_Task()
    {
        // Arrange
        SetupMockPlatform();

        // Act
        var taskId = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test.zip"));

        // Wait for download to start
        await Task.Delay(100);

        // Assert
        var task = _service.GetDownloadTask(taskId);
        Assert.NotNull(task);
        Assert.True(task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Completed);
    }

    [Fact]
    public void GetDownloadTask_Should_Return_Null_For_Unknown_Id()
    {
        // Act
        var task = _service.GetDownloadTask(Guid.NewGuid());

        // Assert
        Assert.Null(task);
    }

    [Fact]
    public void GetAllDownloads_Should_Return_All_Tasks()
    {
        // Arrange
        SetupMockPlatform();
        var taskId1 = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test1.zip"));
        var taskId2 = _service.EnqueueDownload("test", "mod2", Path.Combine(_tempDir, "test2.zip"));

        // Act
        var tasks = _service.GetAllDownloads();

        // Assert
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.Id == taskId1);
        Assert.Contains(tasks, t => t.Id == taskId2);
    }

    [Fact]
    public async Task GetActiveDownloads_Should_Return_Only_Active_Tasks()
    {
        // Arrange
        SetupSlowMockPlatform(); // Use slower mock to keep task active
        var taskId = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test.zip"));

        // Wait to ensure task starts
        await Task.Delay(100);

        // Act
        var tasks = _service.GetActiveDownloads();

        // Assert
        Assert.NotEmpty(tasks);
        Assert.Contains(tasks, t => t.Id == taskId);
    }

    [Fact]
    public void CancelDownload_Should_Cancel_Task()
    {
        // Arrange
        SetupMockPlatform();
        var taskId = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test.zip"));

        // Act
        var result = _service.CancelDownload(taskId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CancelDownload_Should_Return_False_For_Unknown_Task()
    {
        // Act
        var result = _service.CancelDownload(Guid.NewGuid());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ClearCompleted_Should_Remove_Completed_Tasks()
    {
        // Arrange
        SetupMockPlatform();
        var taskId1 = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "test1.zip"));
        var taskId2 = _service.EnqueueDownload("test", "mod2", Path.Combine(_tempDir, "test2.zip"));

        _service.CancelDownload(taskId1); // This makes it completed
        await _service.WaitForDownloadAsync(taskId1).WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        _service.ClearCompleted();

        // Assert
        var tasks = _service.GetAllDownloads();
        Assert.DoesNotContain(tasks, t => t.Id == taskId1);
    }

    [Fact]
    public async Task Download_Task_Should_Complete_Successfully()
    {
        // Arrange
        SetupMockPlatform();
        var targetPath = Path.Combine(_tempDir, "completed.zip");

        // Act
        var taskId = _service.EnqueueDownload("test", "mod1", targetPath);

        // Wait for download to complete
        var maxWait = TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            var task = _service.GetDownloadTask(taskId);
            if (task?.Status == DownloadStatus.Completed || task?.Status == DownloadStatus.Failed)
            {
                break;
            }
            await Task.Delay(50);
        }

        // Assert
        var completedTask = _service.GetDownloadTask(taskId);
        Assert.NotNull(completedTask);
        Assert.Equal(DownloadStatus.Completed, completedTask.Status);
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task Download_Task_Should_Report_Progress()
    {
        // Arrange
        SetupMockPlatform();

        // Act
        var taskId = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "progress.zip"));

        // Wait a bit for progress
        await Task.Delay(200);

        // Assert
        var task = _service.GetDownloadTask(taskId);
        Assert.NotNull(task);
        Assert.NotNull(task.ModName);
    }

    [Fact]
    public async Task Download_Task_Copy_Failure_Should_Preserve_Existing_Target_And_Remove_Partial()
    {
        SetupMockPlatform();
        _mockPlatform.Setup(p => p.DownloadModAsync(It.IsAny<string>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartialCopyStream(fail: true));
        var target = Path.Combine(_tempDir, "existing.zip");
        await File.WriteAllTextAsync(target, "original");
        var id = _service.EnqueueDownload("test", "mod1", target);
        await WaitUntilAsync(() => _service.GetDownloadTask(id)?.Status == DownloadStatus.Failed &&
            Directory.GetFiles(_tempDir).Length == 1);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task Download_Task_Copy_Cancellation_Should_Preserve_Target_And_Ignore_Late_Progress()
    {
        SetupMockPlatform();
        var stream = new PartialCopyStream(fail: false);
        IProgress<DownloadProgress>? reporter = null;
        _mockPlatform.Setup(p => p.DownloadModAsync(It.IsAny<string>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IProgress<DownloadProgress>? progress, CancellationToken _) =>
            {
                reporter = progress;
                return stream;
            });
        var target = Path.Combine(_tempDir, "existing.zip");
        await File.WriteAllTextAsync(target, "original");
        var id = _service.EnqueueDownload("test", "mod1", target);
        await stream.CopyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_service.CancelDownload(id));
        reporter!.Report(new DownloadProgress { TotalBytes = 10, DownloadedBytes = 9 });
        await _service.WaitForDownloadAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Cancelled, _service.GetDownloadTask(id)!.Status);
        Assert.Equal(0, _service.GetDownloadTask(id)!.Progress);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Download_Task_Completion_Should_Ignore_Late_Progress()
    {
        SetupMockPlatform();
        IProgress<DownloadProgress>? reporter = null;
        _mockPlatform.Setup(p => p.DownloadModAsync(It.IsAny<string>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IProgress<DownloadProgress>? progress, CancellationToken _) =>
            {
                reporter = progress;
                return new MemoryStream([1, 2, 3]);
            });
        var id = _service.EnqueueDownload("test", "mod1", Path.Combine(_tempDir, "complete.zip"));
        await WaitUntilAsync(() => _service.GetDownloadTask(id)?.Status == DownloadStatus.Completed);
        reporter!.Report(new DownloadProgress { TotalBytes = 10, DownloadedBytes = 1 });
        Assert.Equal(100, _service.GetDownloadTask(id)!.Progress);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class PartialCopyStream(bool fail) : MemoryStream
    {
        public TaskCompletionSource CopyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2 }, cancellationToken);
            CopyStarted.TrySetResult();
            if (fail) throw new IOException("test copy failure");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private void SetupMockPlatform()
    {
        _mockPlatform.Setup(p => p.GetModInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformModInfo
            {
                Id = "mod1",
                Name = "Test Mod",
                Description = "Test",
                Author = "Author",
                Version = "1.0.0"
            });

        _mockPlatform.Setup(p => p.DownloadModAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<DownloadProgress>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modId, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            {
                var data = System.Text.Encoding.UTF8.GetBytes("Mock mod data");
                var stream = new MemoryStream(data);

                progress?.Report(new DownloadProgress
                {
                    TotalBytes = data.Length,
                    DownloadedBytes = data.Length,
                    StatusMessage = "Download complete"
                });

                return stream;
            });

        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns(_mockPlatform.Object);
    }

    private void SetupSlowMockPlatform()
    {
        _mockPlatform.Setup(p => p.GetModInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformModInfo
            {
                Id = "mod1",
                Name = "Test Mod",
                Description = "Test",
                Author = "Author",
                Version = "1.0.0"
            });

        _mockPlatform.Setup(p => p.DownloadModAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<DownloadProgress>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string modId, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            {
                // Simulate slow download
                await Task.Delay(500, ct);

                var data = System.Text.Encoding.UTF8.GetBytes("Mock mod data");
                var stream = new MemoryStream(data);

                progress?.Report(new DownloadProgress
                {
                    TotalBytes = data.Length,
                    DownloadedBytes = data.Length,
                    StatusMessage = "Download complete"
                });

                return stream;
            });

        _mockRegistry.Setup(r => r.GetPlatform("test")).Returns(_mockPlatform.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var downloads = _service.GetAllDownloads();
        foreach (var task in downloads) _service.CancelDownload(task.Id);
        await Task.WhenAll(downloads.Select(task => _service.WaitForDownloadAsync(task.Id)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }
}
