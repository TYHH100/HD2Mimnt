using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
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
        var viewModel = new TaskCenterViewModel(taskService);

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
}
