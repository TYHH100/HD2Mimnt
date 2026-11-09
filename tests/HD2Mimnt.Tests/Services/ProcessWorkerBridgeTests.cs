using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HD2Mimnt.Tests.Services;

/// <summary>TaskService 与进程 Worker 的桥接语义：终态映射、TaskId 复用、强杀转发。</summary>
public sealed class ProcessWorkerBridgeTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly HD2MimntDbContext _dbContext;
    private readonly ITaskRepository _taskRepository;
    private readonly Mock<IWorkerHostManager> _hostManager = new();
    private readonly TaskService _taskService;
    private readonly TaskCompletionSource<WorkerResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessWorkerBridgeTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_worker_bridge_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _taskRepository = new TaskRepository(_dbContext);

        _hostManager
            .Setup(m => m.StartWorkerAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IProgress<WorkerTaskProgress>?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(),
                It.IsAny<WorkerPromptHandler?>()))
            .ReturnsAsync((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? taskId, WorkerPromptHandler? _) => taskId ?? Guid.NewGuid());
        _hostManager
            .Setup(m => m.WaitForCompletionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(_completion.Task);

        _taskService = new TaskService(_taskRepository, Mock.Of<IServiceProvider>(), _hostManager.Object);
        _taskService.RegisterProcessWorker("Validation");
    }

    [Fact]
    public async Task CompletedResult_ReusesTaskCentreIdAndPersistsResult()
    {
        Guid? startedId = null;
        _hostManager.Setup(m => m.StartWorkerAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IProgress<WorkerTaskProgress>?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(),
                It.IsAny<WorkerPromptHandler?>()))
            .Callback((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? taskId, WorkerPromptHandler? _) => startedId = taskId)
            .ReturnsAsync((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? taskId, WorkerPromptHandler? _) => taskId ?? Guid.NewGuid());

        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        _completion.SetResult(new WorkerResult { Success = true, Result = "Validated 3 mods" });
        await _taskService.WaitForTaskAsync(id);

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(id, startedId);
        Assert.Equal(Core.Entities.TaskStatus.Completed, task!.Status);
        Assert.Equal("Validated 3 mods", task.Result);
    }

    [Fact]
    public async Task PartiallyFailedResult_PreservesStatusAndErrors()
    {
        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        _completion.SetResult(new WorkerResult
        {
            Success = false,
            PartiallyFailed = true,
            Result = "Validated 2 mods, 1 errors",
            Errors = new List<string> { "Broken: Mod directory does not exist" }
        });
        await _taskService.WaitForTaskAsync(id);

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(Core.Entities.TaskStatus.PartiallyFailed, task!.Status);
        Assert.Contains("Broken: Mod directory does not exist", task.Errors);
    }

    [Fact]
    public async Task CrashedResult_FailsTaskWithWorkerError()
    {
        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        _completion.SetResult(new WorkerResult
        {
            Success = false,
            Crashed = true,
            Errors = new List<string> { "Worker process crashed: exited unexpectedly" }
        });
        await _taskService.WaitForTaskAsync(id);

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(Core.Entities.TaskStatus.Failed, task!.Status);
        Assert.Contains(task.Errors, e => e.Contains("crashed"));
    }

    [Fact]
    public async Task CancelledResult_CancelsTaskAndWaitsForWorkerCleanup()
    {
        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        _completion.SetResult(new WorkerResult { Success = false, Cancelled = true });
        await _taskService.WaitForTaskAsync(id);

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(Core.Entities.TaskStatus.Cancelled, task!.Status);
        _hostManager.Verify(m => m.CancelWorkerAsync(It.IsAny<Guid>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cancellation_ForwardsCancelBeforeWorkerFinishes()
    {
        var cancelForwarded = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostManager.Setup(m => m.CancelWorkerAsync(It.IsAny<Guid>()))
            .Callback((Guid id) => cancelForwarded.TrySetResult(id))
            .Returns(Task.CompletedTask);

        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        await WaitUntilRunningAsync(id);

        var cancel = _taskService.CancelTaskAsync(id);
        Assert.Equal(id, await cancelForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(cancel.IsCompleted, "取消必须等待 Worker 清理完成后再返回");

        _completion.SetResult(new WorkerResult { Success = false, Cancelled = true });
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(Core.Entities.TaskStatus.Cancelled, task!.Status);
    }

    [Fact]
    public async Task ForceTerminate_KillsWorkerBeforePersistingFinalState()
    {
        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        await WaitUntilRunningAsync(id);

        await _taskService.ForceTerminateTaskAsync(id);

        var task = await _taskService.GetTaskAsync(id);
        Assert.Equal(Core.Entities.TaskStatus.ForceTerminated, task!.Status);
        _hostManager.Verify(m => m.ForceTerminateWorkerAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task RunProcessTaskAsync_PassesArgumentsPromptHandlerAndReturnsData()
    {
        Dictionary<string, string>? capturedArguments = null;
        WorkerPromptHandler? capturedPrompt = null;
        _hostManager.Setup(m => m.StartWorkerAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IProgress<WorkerTaskProgress>?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(),
                It.IsAny<WorkerPromptHandler?>()))
            .Callback((string _, Dictionary<string, string> arguments, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? _, WorkerPromptHandler? prompts) =>
            {
                capturedArguments = arguments;
                capturedPrompt = prompts;
            })
            .ReturnsAsync((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? taskId, WorkerPromptHandler? _) => taskId ?? Guid.NewGuid());

        _completion.SetResult(new WorkerResult { Success = true, Result = "ok", Data = "[\"imported\"]" });
        var promptHandler = new WorkerPromptHandler((_, _) =>
            Task.FromResult(new Core.IPC.WorkerPromptResponse { Accepted = true }));

        var outcome = await _taskService.RunProcessTaskAsync("导入", "Validation",
            new Dictionary<string, string> { ["Paths"] = "[\"a.zip\"]" }, promptHandler: promptHandler);

        Assert.Equal(Core.Entities.TaskStatus.Completed, outcome.Task!.Status);
        Assert.Equal("[\"imported\"]", outcome.Data);
        Assert.Equal("[\"a.zip\"]", capturedArguments!["Paths"]);
        Assert.Same(promptHandler, capturedPrompt);
    }

    [Fact]
    public async Task RunProcessTaskAsync_SuppressesGenericCompletionNotification()
    {
        TaskCompletedEventArgs? completed = null;
        _taskService.TaskCompleted += (_, args) => completed = args;
        _completion.SetResult(new WorkerResult { Success = true });

        await _taskService.RunProcessTaskAsync("导入", "Validation");

        Assert.NotNull(completed);
        Assert.False(completed!.NotifyUser);
    }

    [Fact]
    public async Task StartTaskAsync_KeepsGenericCompletionNotification()
    {
        TaskCompletedEventArgs? completed = null;
        _taskService.TaskCompleted += (_, args) => completed = args;
        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);
        _completion.SetResult(new WorkerResult { Success = true });
        await _taskService.WaitForTaskAsync(id);

        Assert.NotNull(completed);
        Assert.True(completed!.NotifyUser);
    }

    [Fact]
    public async Task WorkerProgress_IsForwardedToTaskRecord()
    {
        IProgress<WorkerTaskProgress>? captured = null;
        _hostManager.Setup(m => m.StartWorkerAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<IProgress<WorkerTaskProgress>?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(),
                It.IsAny<WorkerPromptHandler?>()))
            .Callback((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? progress,
                CancellationToken _, Guid? _, WorkerPromptHandler? _) => captured = progress)
            .ReturnsAsync((string _, Dictionary<string, string> _, IProgress<WorkerTaskProgress>? _,
                CancellationToken _, Guid? taskId, WorkerPromptHandler? _) => taskId ?? Guid.NewGuid());

        var id = await _taskService.CreateTaskAsync("Validate", "Validation");
        await _taskService.StartTaskAsync(id);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (captured == null && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.NotNull(captured);

        captured.Report(new WorkerTaskProgress
        {
            Progress = 42,
            Stage = "Validating Mods",
            CurrentItem = "Armor A",
            Message = "Validating Armor A"
        });

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Core.Entities.ModTask? stored;
        do
        {
            await Task.Delay(50);
            stored = await _taskService.GetTaskAsync(id);
        } while (stored?.CurrentItem != "Armor A" && DateTime.UtcNow < deadline);

        Assert.Equal("Armor A", stored!.CurrentItem);
        Assert.Equal("Validating Mods", stored.Stage);

        _completion.TrySetResult(new WorkerResult { Success = true, Result = "done" });
        await _taskService.WaitForTaskAsync(id);
    }

    private async Task WaitUntilRunningAsync(Guid id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var task = await _taskService.GetTaskAsync(id);
            if (task?.Status == Core.Entities.TaskStatus.Running) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Task did not reach Running state");
    }

    public void Dispose()
    {
        _completion.TrySetResult(new WorkerResult { Success = false, Cancelled = true });
        _taskService.CancelAllAndWaitAsync().GetAwaiter().GetResult();
        _dbContext.Dispose();
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
    }
}