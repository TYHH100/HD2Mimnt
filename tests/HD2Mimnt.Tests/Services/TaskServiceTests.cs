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
        await Task.Delay(1500);

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
        await Task.Delay(3000);

        // Assert
        var task = await _taskService.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(Core.Entities.TaskStatus.Completed, task.Status);
        Assert.NotNull(task.EndTime);
        Assert.True(task.EndTime > task.StartTime);
    }

    [Fact]
    public async Task GetHistory_ShouldReturnCompletedTasks()
    {
        // Arrange
        var taskId = await _taskService.CreateTaskAsync("History Task", "Import");
        await _taskService.StartTaskAsync(taskId);
        await Task.Delay(3000); // 等待完成

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
        await Task.Delay(5000); // 等待完成

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
        _dbContext.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
