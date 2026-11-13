using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class TaskCenterViewModelTests
{
    [Fact]
    public async Task AutoRefresh_ShouldReloadWhileRunningAndStopCleanly()
    {
        var repository = new Mock<ITaskRepository>();
        repository.Setup(item => item.GetHistoryAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModTask>());
        repository.Setup(item => item.GetRunningTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModTask>());

        var taskService = new TaskService(repository.Object, Mock.Of<IServiceProvider>());
        var viewModel = new TaskCenterViewModel(taskService, Mock.Of<IConfirmationService>());

        viewModel.StartAutoRefresh(TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        viewModel.StopAutoRefresh();
        var callsAfterStop = repository.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ITaskRepository.GetHistoryAsync));

        await Task.Delay(80);

        Assert.True(callsAfterStop >= 2);
        Assert.Equal(callsAfterStop, repository.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ITaskRepository.GetHistoryAsync)));
    }

    [Fact]
    public async Task ClearFinished_ShouldDeleteOnlyFinishedTasksAfterConfirmation()
    {
        var repository = new Mock<ITaskRepository>();
        var (finished, running) = SeedTasks(repository);
        var viewModel = CreateViewModel(repository, confirmed: true);

        await viewModel.LoadAsync();
        Assert.True(viewModel.HasFinishedTasks);

        await viewModel.ClearFinishedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { finished.Id }, DeletedIds(repository));
    }

    [Fact]
    public async Task ClearFinished_ShouldKeepRecordsWhenConfirmationDeclined()
    {
        var repository = new Mock<ITaskRepository>();
        SeedTasks(repository);
        var viewModel = CreateViewModel(repository, confirmed: false);

        await viewModel.LoadAsync();
        await viewModel.ClearFinishedCommand.ExecuteAsync(null);

        Assert.Empty(DeletedIds(repository));
    }

    [Fact]
    public async Task ClearFinished_ShouldDoNothingWithoutFinishedTasks()
    {
        var repository = new Mock<ITaskRepository>();
        var (_, running) = SeedTasks(repository, finishedCount: 0);
        var viewModel = CreateViewModel(repository, confirmed: true);

        await viewModel.LoadAsync();
        Assert.False(viewModel.HasFinishedTasks);

        await viewModel.ClearFinishedCommand.ExecuteAsync(null);

        Assert.Empty(DeletedIds(repository));
        Assert.Equal(running.Id, Assert.Single(viewModel.Tasks).Id);
    }

    [Fact]
    public async Task Delete_ShouldRemoveFinishedTaskAndRejectRunningTask()
    {
        var repository = new Mock<ITaskRepository>();
        var (finished, running) = SeedTasks(repository);
        var viewModel = CreateViewModel(repository, confirmed: true);

        await viewModel.LoadAsync();
        await viewModel.DeleteCommand.ExecuteAsync(running);
        Assert.Empty(DeletedIds(repository));

        await viewModel.DeleteCommand.ExecuteAsync(finished);

        Assert.Equal(new[] { finished.Id }, DeletedIds(repository));
        repository.Setup(item => item.GetHistoryAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModTask>());
        await viewModel.LoadAsync();
        Assert.False(viewModel.HasFinishedTasks);
    }

    private static TaskCenterViewModel CreateViewModel(Mock<ITaskRepository> repository, bool confirmed)
    {
        var confirmation = new Mock<IConfirmationService>();
        confirmation.Setup(item => item.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(confirmed);
        var taskService = new TaskService(repository.Object, Mock.Of<IServiceProvider>());
        return new TaskCenterViewModel(taskService, confirmation.Object);
    }

    [Fact]
    public async Task Load_ShouldUpdateRunningTaskInPlaceSoProgressBarDoesNotRestart()
    {
        var repository = new Mock<ITaskRepository>();
        var progress = 34d;
        var message = "已同步 34/78";
        repository.Setup(item => item.GetHistoryAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModTask>());
        // 每次查询都返回新的实体实例，模拟仓储从数据库重建对象。
        repository.Setup(item => item.GetRunningTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[]
            {
                new ModTask
                {
                    Id = RunningId, Name = "同步到 HellForge", Type = "HellForgeSync",
                    Status = Core.Entities.TaskStatus.Running, Progress = progress, Message = message
                }
            });
        var viewModel = CreateViewModel(repository, confirmed: true);

        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.Tasks);
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        progress = 52;
        message = "已同步 40/78";
        await viewModel.LoadAsync();

        Assert.Same(item, Assert.Single(viewModel.Tasks));
        Assert.Equal(52, item.Progress);
        Assert.Equal("已同步 40/78", item.Message);
        Assert.Contains(nameof(ModTask.Progress), changed);
    }

    [Fact]
    public async Task Load_ShouldDropTasksMissingFromSnapshot()
    {
        var repository = new Mock<ITaskRepository>();
        var present = true;
        repository.Setup(item => item.GetHistoryAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => present
                ? new[] { new ModTask { Id = RunningId, Name = "历史", Status = Core.Entities.TaskStatus.Completed, EndTime = DateTime.UtcNow } }
                : Array.Empty<ModTask>());
        repository.Setup(item => item.GetRunningTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ModTask>());
        var viewModel = CreateViewModel(repository, confirmed: true);

        await viewModel.LoadAsync();
        Assert.Single(viewModel.Tasks);

        present = false;
        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Tasks);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasFinishedTasks);
    }

    private static readonly Guid RunningId = Guid.Parse("3e67d398-cb94-4079-beff-de65aaeca288");

    private static (ModTask Finished, ModTask Running) SeedTasks(Mock<ITaskRepository> repository, int finishedCount = 1)
    {
        var finished = Enumerable.Range(0, finishedCount)
            .Select(index => new ModTask
            {
                Id = Guid.NewGuid(),
                Name = $"历史 {index}",
                Status = Core.Entities.TaskStatus.Completed,
                EndTime = DateTime.UtcNow
            })
            .ToList();
        var running = new ModTask { Id = Guid.NewGuid(), Name = "部署", Status = Core.Entities.TaskStatus.Running };
        var stored = finished.Append(running).ToList();
        repository.Setup(item => item.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        repository.Setup(item => item.GetHistoryAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(finished);
        repository.Setup(item => item.GetRunningTasksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { running });
        repository.Setup(item => item.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => stored.SingleOrDefault(task => task.Id == id));
        return (finished.FirstOrDefault()!, running);
    }

    private static List<Guid> DeletedIds(Mock<ITaskRepository> repository) => repository.Invocations
        .Where(invocation => invocation.Method.Name == nameof(ITaskRepository.DeleteAsync))
        .Select(invocation => (Guid)invocation.Arguments[0]!)
        .ToList();
}
