using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.Services;

public class TaskServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly ITaskRepository _taskRepository;
    private readonly IModRepository _modRepository;
    private readonly TaskService _taskService;
    private readonly string _testDbPath;

    public TaskServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_tasks_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _taskRepository = new TaskRepository(_dbContext);
        _modRepository = new ModRepository(_dbContext);

        var services = new ServiceCollection();
        services.AddSingleton(_taskRepository);
        services.AddSingleton(_modRepository);
        var serviceProvider = services.BuildServiceProvider();

        _taskService = new TaskService(_taskRepository, serviceProvider);

        // 注册测试 Workers（进程 Worker 由 WorkerProcessIntegrationTests 覆盖）
        _taskService.RegisterWorker(new QuickWorker("Import"));
        _taskService.RegisterWorker(new SteppedWorker());
        _taskService.RegisterWorker(new PartialWorker());
        _taskService.RegisterWorker(new BlockingWorker());
    }

    [Fact]
    public async Task CreateTask_ShouldCreateNewTask()
    {
        // Act
        var taskId = await _taskService.CreateTaskAsync("Test Import", "Import");

        // Assert
        Assert.NotEqual(Guid.Empty, taskId);

        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal("Test Import", task.Name);
        Assert.Equal("Import", task.Type);
        Assert.Equal(Core.Entities.TaskStatus.Waiting, task.Status);
    }

    [Fact]
    public async Task RuntimeOperation_ShouldWaitForCancellationCleanupAndPersistFinalState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = _taskService.RunTaskAsync("N 网下载：测试", "PlatformDownload", async (progress, token) =>
        {
            progress.Report(new() { Progress = 35, Stage = "Downloading", CurrentItem = "armor.zip", Message = "Downloading" });
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleaning.SetResult(); await release.Task; }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = Assert.Single(await _taskService.GetRunningTasksAsync());
            Assert.Equal(35, running.Progress);
            Assert.Equal("armor.zip", running.CurrentItem);
            var cancellation = _taskService.CancelTaskAsync(running.Id);
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cancellation.IsCompleted);
            Assert.False(operation.IsCompleted);
            release.SetResult();
            await cancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            var stored = await _taskService.GetTaskAsync(running.Id);
            Assert.Equal(Core.Entities.TaskStatus.Cancelled, stored!.Status);
            Assert.NotNull(stored.EndTime);
            Assert.Empty(await _taskService.GetRunningTasksAsync());
        }
        finally { release.TrySetResult(); await _taskService.CancelAllAndWaitAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeOperation_ShouldPreserveSuccessOrOriginalFailure(bool fail)
    {
        var error = new InvalidDataException("download rejected");
        var operation = _taskService.RunTaskAsync("A 网下载：测试", "PlatformDownload", (progress, _) =>
        {
            progress.Report(new() { Progress = 85, Stage = "Downloading", Message = "file received" });
            if (fail) throw error;
            return Task.CompletedTask;
        });
        if (fail) Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => operation));
        else await operation;
        var stored = Assert.Single(await _taskService.GetHistoryAsync());
        Assert.Equal(fail ? Core.Entities.TaskStatus.Failed : Core.Entities.TaskStatus.Completed, stored.Status);
        Assert.Equal(fail ? "download rejected" : "file received", stored.Message);
        Assert.NotNull(stored.EndTime);
        Assert.Empty(await _taskService.GetRunningTasksAsync());
    }

    [Fact]
    public async Task StartTask_ShouldExecuteWorker()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("Test Scan", "Scan");

        // Act
        await _taskService.StartTaskAsync(taskId);

        // 等待任务开始执行
        await Task.Delay(500);

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.True(task.Status == Core.Entities.TaskStatus.Running || task.Status == Core.Entities.TaskStatus.Completed);
    }

    [Fact]
    public async Task StartTask_ShouldUpdateProgress()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("Test Progress", "Scan");

        // Act
        await _taskService.StartTaskAsync(taskId);

        // 等待任务执行一段时间
        await Task.Delay(1500);

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.True(task.Progress > 0);
    }

    [Fact]
    public async Task CancelTask_ShouldCancelRunningTask()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("Test Cancel", "Scan");
        await _taskService.StartTaskAsync(taskId);
        await Task.Delay(500);

        // Act
        await _taskService.CancelTaskAsync(taskId);

        // 等待取消完成

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(Core.Entities.TaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public async Task GetRunningTasks_ShouldReturnRunningTasks()
    {
        // Arrange
        var taskId1 = await _taskService.CreateTaskAsync("Running Task 1", "Scan");
        var taskId2 = await _taskService.CreateTaskAsync("Running Task 2", "Import");

        await _taskService.StartTaskAsync(taskId1);
        await _taskService.StartTaskAsync(taskId2);
        await Task.Delay(300);

        // Act
        var runningTasks = await _taskService.GetRunningTasksAsync();

        // Assert
        var taskList = runningTasks.ToList();
        Assert.NotEmpty(taskList);
        Assert.Contains(taskList, t => t.Id == taskId1);
        Assert.Contains(taskList, t => t.Id == taskId2);
    }

    [Fact]
    public async Task TaskCompletion_ShouldSetEndTime()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("Quick Task", "Import");

        // Act
        await _taskService.StartTaskAsync(taskId);

        // 等待任务完成
        await _taskService.WaitForTaskAsync(taskId);

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(Core.Entities.TaskStatus.Completed, task.Status);
        Assert.NotNull(task.EndTime);
        Assert.True(task.EndTime > task.StartTime);
    }

    [Fact]
    public async Task TaskCompletion_ShouldRaiseCompletionEvent()
    {
        var completed = new TaskCompletionSource<ModTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskService.TaskCompleted += (_, args) => completed.TrySetResult(args.Task);
        var taskId = await _taskService.CreateTaskAsync("Notification Task", "Import");

        await _taskService.StartTaskAsync(taskId);
        var task = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(taskId, task.Id);
        Assert.Equal(Core.Entities.TaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task ForceTerminateTask_ShouldPersistAndRaiseOneCompletionEvent()
    {
        var completed = new TaskCompletionSource<ModTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCount = 0;
        _taskService.TaskCompleted += (_, args) =>
        {
            Interlocked.Increment(ref eventCount);
            completed.TrySetResult(args.Task);
        };
        var taskId = await _taskService.CreateTaskAsync("Force terminate task", "Blocking");

        await _taskService.StartTaskAsync(taskId);
        await _taskService.ForceTerminateTaskAsync(taskId);
        var eventTask = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        var storedTask = await _taskService.GetTaskAsync(taskId);

        Assert.Equal(taskId, eventTask.Id);
        Assert.Equal(Core.Entities.TaskStatus.ForceTerminated, eventTask.Status);
        Assert.NotNull(storedTask);
        Assert.Equal(Core.Entities.TaskStatus.ForceTerminated, storedTask.Status);
        Assert.Equal(1, eventCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetachedRunningRecord_ShouldBeFinalizedByCancelOrForceTerminate(bool forceTerminate)
    {
        // 模拟上次进程异常退出后遗留的“运行中”记录：数据库里是 Running，但没有内存执行句柄。
        var taskId = await AddDetachedRecordAsync(Core.Entities.TaskStatus.Running);
        Assert.Contains(await _taskService.GetRunningTasksAsync(), task => task.Id == taskId);

        if (forceTerminate) await _taskService.ForceTerminateTaskAsync(taskId);
        else await _taskService.CancelTaskAsync(taskId);

        var stored = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(stored);
        Assert.Equal(forceTerminate
            ? Core.Entities.TaskStatus.ForceTerminated
            : Core.Entities.TaskStatus.Cancelled, stored.Status);
        Assert.NotNull(stored.EndTime);
        Assert.Empty(await _taskService.GetRunningTasksAsync());
    }

    [Fact]
    public async Task DetachedFinishedRecord_ShouldKeepOriginalTerminalState()
    {
        var taskId = await AddDetachedRecordAsync(Core.Entities.TaskStatus.Completed);
        var before = await _taskService.GetTaskAsync(taskId);

        await _taskService.ForceTerminateTaskAsync(taskId);
        await _taskService.CancelTaskAsync(taskId);

        var stored = await _taskService.GetTaskAsync(taskId);
        Assert.Equal(before!.Status, stored!.Status);
        Assert.Equal(before.EndTime, stored.EndTime);
    }

    [Fact]
    public async Task RecoverInterruptedTasks_ShouldFinalizeLeftoverRecordsAndKeepLiveTasks()
    {
        var leftover = await AddDetachedRecordAsync(Core.Entities.TaskStatus.Running);
        var waiting = await AddDetachedRecordAsync(Core.Entities.TaskStatus.Waiting);
        var liveId = await _taskService.CreateTaskAsync("Live task", "Blocking");
        await _taskService.StartTaskAsync(liveId);
        try
        {
            var recovered = await _taskService.RecoverInterruptedTasksAsync();

            Assert.Equal(2, recovered);
            foreach (var id in new[] { leftover, waiting })
            {
                var stored = await _taskService.GetTaskAsync(id);
                Assert.Equal(Core.Entities.TaskStatus.Failed, stored!.Status);
                Assert.Equal("Interrupted", stored.Stage);
                Assert.NotNull(stored.EndTime);
            }

            // 本次进程真正在跑的任务不能被收尾。
            var live = await _taskService.GetTaskAsync(liveId);
            Assert.Equal(Core.Entities.TaskStatus.Running, live!.Status);
        }
        finally
        {
            await _taskService.CancelTaskAsync(liveId);
        }
    }

    private async Task<Guid> AddDetachedRecordAsync(Core.Entities.TaskStatus status)
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Name = "遗留任务",
            Type = "HellForgeSync",
            Status = status,
            Progress = 34,
            Stage = "Running",
            Message = "已同步 34/78",
            StartTime = DateTime.UtcNow.AddHours(-1)
        };
        await _taskRepository.AddAsync(task);
        return task.Id;
    }

    [Fact]
    public async Task GetHistory_ShouldReturnCompletedTasks()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("History Task", "Import");
        await _taskService.StartTaskAsync(taskId);
        await _taskService.WaitForTaskAsync(taskId);

        // Act
        var history = await _taskService.GetHistoryAsync();

        // Assert
        var historyList = history.ToList();
        Assert.NotEmpty(historyList);
        Assert.Contains(historyList, t => t.Id == taskId);
    }

    [Fact]
    public async Task WorkerSettingPartiallyFailed_ShouldPreserveStatus()
    {
        var taskId = await _taskService.CreateTaskAsync("Partial Task", "Partial");

        await _taskService.StartTaskAsync(taskId);
        await _taskService.WaitForTaskAsync(taskId);

        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(Core.Entities.TaskStatus.PartiallyFailed, task.Status);
        Assert.Equal("Partial done", task.Result);
        Assert.NotEmpty(task.Errors);
    }

    public void Dispose()
    {
        _taskService.CancelAllAndWaitAsync().GetAwaiter().GetResult();
        _dbContext.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    private sealed class BlockingWorker : IWorker
    {
        public string Type => "Blocking";

        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new TaskProgress { Progress = 10, Stage = "Running", Message = "Waiting" });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class QuickWorker(string type) : IWorker
    {
        public string Type => type;

        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new TaskProgress { Progress = 50, Stage = "Working", Message = "Working" });
            // 保留可观测的运行窗口，供“运行中任务”断言；整体仍在 1 秒内完成。
            await Task.Delay(800, cancellationToken);
            progress.Report(new TaskProgress { Progress = 100, Stage = "Completed", Message = "Done" });
            task.Result = "ok";
        }
    }

    private sealed class PartialWorker : IWorker
    {
        public string Type => "Partial";

        public Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new TaskProgress { Progress = 60, Stage = "Validating", Message = "Some items failed" });
            task.Errors.Add("item 1 failed");
            task.Result = "Partial done";
            task.Status = Core.Entities.TaskStatus.PartiallyFailed;
            return Task.CompletedTask;
        }
    }

    private sealed class SteppedWorker : IWorker
    {
        public string Type => "Scan";

        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            for (var step = 1; step <= 5; step++)
            {
                progress.Report(new TaskProgress { Progress = step * 20, Stage = "Scanning", Message = $"Step {step}" });
                await Task.Delay(200, cancellationToken);
            }
        }
    }
}
