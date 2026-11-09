using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 真实 HD2Mimnt.Worker 进程的生命周期验证。隔离数据目录经 HD2MIMNT_DATA_PATH 传给子进程，
/// 该变量是进程级状态，整组禁用并行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerProcessCollection
{
    public const string Name = "WorkerProcess";
}

[Collection(WorkerProcessCollection.Name)]
public sealed class WorkerProcessIntegrationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _root;
    private readonly string? _previousDataPath;
    private readonly WorkerHostManager _manager = new();

    public WorkerProcessIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HD2MimntWorkerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        _previousDataPath = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", _root);
    }

    [Fact]
    public async Task Validation_EmptyLibrary_CompletesAndReportsProgress()
    {
        await InitializeLibraryAsync();
        var progress = new List<WorkerTaskProgress>();

        var taskId = await _manager.StartWorkerAsync("Validation", new Dictionary<string, string>(),
            new SynchronousProgress<WorkerTaskProgress>(progress.Add));
        var result = await _manager.WaitForCompletionAsync(taskId).WaitAsync(Timeout);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.False(result.Crashed);
        Assert.False(result.PartiallyFailed);
        Assert.Contains("Validated 0 mods", result.Result);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Validation_MissingModDirectory_ReportsPartiallyFailed()
    {
        var missing = await AddModAsync("Broken", Path.Combine(_root, "mods", "does-not-exist"));
        var valid = await AddModAsync("Valid", await CreateModDirectoryAsync("valid"));

        var taskId = await _manager.StartWorkerAsync("Validation", new Dictionary<string, string>());
        var result = await _manager.WaitForCompletionAsync(taskId).WaitAsync(Timeout);

        Assert.False(result.Success);
        Assert.False(result.Crashed);
        Assert.True(result.PartiallyFailed);
        Assert.Contains(result.Errors, e => e.Contains(missing.Name));
        Assert.DoesNotContain(result.Errors, e => e.Contains(valid.Name));
    }

    [Fact]
    public async Task UnknownWorkerType_ReportsErrorWithoutCrashing()
    {
        await InitializeLibraryAsync();

        var taskId = await _manager.StartWorkerAsync("NoSuchWorker", new Dictionary<string, string>());
        var result = await _manager.WaitForCompletionAsync(taskId).WaitAsync(Timeout);

        Assert.False(result.Success);
        Assert.False(result.Crashed);
        Assert.Contains(result.Errors, e => e.Contains("Unknown worker type"));
    }

    [Fact]
    public async Task WorkerThatNeverConnects_FailsWithTimeout()
    {
        // 用一个会立即退出的系统程序代替 Worker.exe：永远不会连接管道。
        using var manager = new WorkerHostManager(
            workerExecutablePath: Path.Combine(Environment.SystemDirectory, "where.exe"),
            connectTimeout: TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            manager.StartWorkerAsync("Validation", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ForceTerminate_StopsRunningWorker()
    {
        await InitializeLibraryAsync();
        var taskId = await _manager.StartWorkerAsync("Validation", new Dictionary<string, string>());

        await _manager.ForceTerminateWorkerAsync(taskId);

        Assert.False(await _manager.IsWorkerRunningAsync(taskId));
    }

    [Fact]
    public async Task StubWorker_CompletesAndReportsProgress()
    {
        using var manager = CreateStubManager();
        var progress = new List<WorkerTaskProgress>();

        var taskId = await manager.StartWorkerAsync("Stub", new Dictionary<string, string> { ["Behavior"] = "complete" },
            new SynchronousProgress<WorkerTaskProgress>(progress.Add));
        var result = await manager.WaitForCompletionAsync(taskId).WaitAsync(Timeout);

        Assert.True(result.Success);
        Assert.False(result.Crashed);
        Assert.Equal("stub done", result.Result);
        Assert.Contains(progress, item => item.Progress == 50);
    }

    [Fact]
    public async Task StubWorker_Crash_IsReportedWithDiagnostics()
    {
        using var manager = CreateStubManager();

        var taskId = await manager.StartWorkerAsync("Stub", new Dictionary<string, string> { ["Behavior"] = "crash" });
        var result = await manager.WaitForCompletionAsync(taskId).WaitAsync(Timeout);

        Assert.True(result.Crashed);
        var error = Assert.Single(result.Errors);
        Assert.Contains("exited unexpectedly", error);
        Assert.Contains("stub worker intentionally crashed", error);
        Assert.False(await manager.IsWorkerRunningAsync(taskId));
    }

    [Fact]
    public async Task StubWorker_IgnoringCancel_IsKilledAfterGracePeriod()
    {
        using var manager = CreateStubManager(TimeSpan.FromSeconds(1));

        var taskId = await manager.StartWorkerAsync("Stub", new Dictionary<string, string> { ["Behavior"] = "hang" });
        var completion = manager.WaitForCompletionAsync(taskId);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await manager.CancelWorkerAsync(taskId);
        var result = await completion.WaitAsync(Timeout);

        Assert.True(result.Cancelled);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(0.9), "挂起的 Worker 必须等宽限期结束后才被结束");
        Assert.False(await manager.IsWorkerRunningAsync(taskId));
    }

    [Fact]
    public async Task StubWorker_GracefulCancel_ReportsCancelled()
    {
        using var manager = CreateStubManager();

        var taskId = await manager.StartWorkerAsync("Stub", new Dictionary<string, string> { ["Behavior"] = "graceful" });
        var completion = manager.WaitForCompletionAsync(taskId);

        await manager.CancelWorkerAsync(taskId);
        var result = await completion.WaitAsync(Timeout);

        Assert.True(result.Cancelled);
        Assert.False(result.Crashed);
        Assert.False(await manager.IsWorkerRunningAsync(taskId));
    }

    private static WorkerHostManager CreateStubManager(TimeSpan? cancelGracePeriod = null) => new(
        workerExecutablePath: Path.Combine(AppContext.BaseDirectory, "HD2Mimnt.WorkerStub.exe"),
        cancelGracePeriod: cancelGracePeriod);

    private async Task InitializeLibraryAsync()
    {
        using var context = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await context.InitializeDatabaseAsync();
    }

    private async Task<Core.Entities.Mod> AddModAsync(string name, string path)
    {
        using var context = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await context.InitializeDatabaseAsync();
        var repository = new ModRepository(context);
        var mod = new Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Author = "Test",
            Version = "1.0.0",
            Description = "Test",
            Path = path,
            Category = Core.Enums.ModCategory.Gameplay,
            Tags = new List<string>(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };
        await repository.AddAsync(mod);
        return mod;
    }

    private async Task<string> CreateModDirectoryAsync(string name)
    {
        var directory = Path.Combine(_root, "mods", name);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            """{"name":"Valid Mod","version":"1.0.0","description":"test"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "data.patch_0"), "payload");
        return directory;
    }

    public void Dispose()
    {
        _manager.Dispose();
        Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", _previousDataPath);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}