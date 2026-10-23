using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Workers;
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

        // 注册测试 Workers
        _taskService.RegisterWorker(new ImportWorker(_modRepository));
        _taskService.RegisterWorker(new ScanWorker(_modRepository));
        _taskService.RegisterWorker(new ValidationWorker(_modRepository));
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
    public async Task ValidationWorker_ShouldReportErrors()
    {
        // Arrange - 先添加一些 Mod
        for (int i = 0; i < 10; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Test Mod {i}",
                Author = "Test Author",
                Version = "1.0.0",
                Description = "Test",
                Path = $"/path/to/mod{i}",
                Category = ModCategory.Gameplay,
                Tags = new List<string>(),
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                ImportedAt = DateTime.UtcNow
            };
            await _modRepository.AddAsync(mod);
        }

        var taskId = await _taskService.CreateTaskAsync("Validation Task", "Validation");

        // Act
        await _taskService.StartTaskAsync(taskId);
        await _taskService.WaitForTaskAsync(taskId);

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.True(task.Status == Core.Entities.TaskStatus.Completed || task.Status == Core.Entities.TaskStatus.PartiallyFailed);

        if (task.Errors.Any())
        {
            Assert.Equal(Core.Entities.TaskStatus.PartiallyFailed, task.Status);
        }
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
}
